#!/usr/bin/env node
// Runs the in-game corrector-QP microbenchmark (Diagnostics/QpBench.cs) inside a real
// browser and prints what it wrote to the console, so the wasm cost of the sim's hot solver
// is a measurement rather than an extrapolation.
//
//   node MTile.Web/smoke/qp_bench.js <url> [--headful] [--chrome <path>]
//
// It serves no files itself — point it at a static server over a PUBLISHED (AOT) wwwroot.
// A `dotnet run --project MTile.Web` dev server is interpreted, not AOT, and reports numbers
// ~15x off; see CLAUDE.md.
//
// Needs puppeteer-core (`npm i puppeteer-core`) and a local Chrome. The Playwright/Python
// scripts next door are the Linux dev-box equivalents; this one exists because the Windows
// box has node and Chrome but no usable python.
const puppeteer = require('puppeteer-core');

let url = process.argv[2];
if (!url) { console.error('usage: qp_bench.js <url> [--headful] [--chrome <path>]'); process.exit(2); }
if (!/qpbench/.test(url)) url += (url.includes('?') ? '&' : '?') + 'qpbench=1';
const headful = process.argv.includes('--headful');
const chromeArg = process.argv.indexOf('--chrome');
const chromePath = chromeArg >= 0 ? process.argv[chromeArg + 1]
  : 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';

(async () => {
  const browser = await puppeteer.launch({
    executablePath: chromePath,
    headless: headful ? false : 'new',
    // SwiftShader keeps this working without a GPU. It only affects rendering; the
    // benchmark is pure CPU wasm, so it does not colour the number.
    args: ['--enable-unsafe-swiftshader', '--use-angle=swiftshader', '--disable-dev-shm-usage'],
  });
  const page = await browser.newPage();
  await page.setViewport({ width: 1280, height: 900 });

  const lines = [];
  page.on('console', m => { const t = m.text(); lines.push(t); if (/\[perf\]|qp bench/.test(t)) console.log(t); });
  page.on('pageerror', e => console.error('PAGE ERROR:', String(e).slice(0, 500)));

  await page.goto(url, { waitUntil: 'load', timeout: 120000 });

  // Blazor + AOT boot is slow; wait for the loading screen to go, then start the game so
  // Game1.Update (which owns the F8 hotkey) is actually running.
  await page.waitForFunction('!document.getElementById("loading")', { timeout: 180000 });
  try {
    await page.waitForFunction(
      "!!Array.from(document.querySelectorAll('button,a')).find(e => /solo/i.test(e.textContent))",
      { timeout: 60000 });
    await page.evaluate(
      "Array.from(document.querySelectorAll('button,a')).find(e => /solo/i.test(e.textContent)).click()");
  } catch { console.error('note: no Solo control found — assuming the game is already running'); }

  await new Promise(r => setTimeout(r, 8000));   // let the sim settle and tier-up warm

  // Chrome owns F8 as a browser shortcut, so a driver cannot deliver it to the canvas.
  // ?qpbench=1 in the URL arms the same bench from the web host instead (Index.razor.cs).
  const canvas = await page.$("#theCanvas");
  if (canvas) await canvas.click();

  // The bench blocks the frame loop for a couple of seconds by design.
  const deadline = Date.now() + 120000;
  while (Date.now() < deadline && !lines.some(l => /qp bench/.test(l)))
    await new Promise(r => setTimeout(r, 500));

  const hit = lines.filter(l => /qp bench/.test(l));
  if (!hit.length) {
    console.error('no qp bench output. last console lines:');
    for (const l of lines.slice(-25)) console.error('  ' + l.slice(0, 300));
    await browser.close();
    process.exit(1);
  }
  await browser.close();
})();
