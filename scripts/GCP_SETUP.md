# GCP dev box — setup notes

Headless build/test box for MTile (.NET 8) on Google Compute Engine. Companion
to `vm-bootstrap.sh` (formerly `ec2-bootstrap.sh`; the script is cloud-agnostic).

The intended flow is: create the instance, SSH in, run two commands. No
startup-script metadata needed (though it works — see the note at the bottom).

## 0. One-time prereqs (local)

- `gcloud` CLI installed and initialized: `gcloud init` (picks project + default
  region/zone). Check with `gcloud config list`.
- A GitHub **deploy key** for the repo (read-only). If you still have
  `~/.ssh/mtile_deploy` from the EC2 setup, reuse it — it's tied to the GitHub
  repo, not to AWS. Otherwise:

```bash
ssh-keygen -t ed25519 -f ~/.ssh/mtile_deploy -N "" -C "mtile-gcp"
# then add mtile_deploy.pub under GitHub repo Settings → Deploy keys (read-only)
```

## 1. Create the instance

```bash
gcloud compute instances create mtile-dev \
  --machine-type=e2-standard-2 \
  --image-family=ubuntu-2404-lts-amd64 \
  --image-project=ubuntu-os-cloud \
  --boot-disk-size=30GB
```

Notes:
- `e2-standard-2` = 2 vCPU / 8 GB, comfortable for `dotnet build` + tests
  (rough equivalent of the old `t3.large`).
- No security-group step: GCP's `default` network ships with a
  `default-allow-ssh` firewall rule, and `gcloud compute ssh` manages SSH keys
  for you (no `--key-name`, no `.pem` files).
- Add `--zone=us-west1-b` (or wherever) if you didn't set a default zone.
- Cheaper option for disposable boxes: add `--provisioning-model=SPOT`
  (can be preempted at any time).

## 2. Copy the deploy key + SSH in

Skip the scp if the instance already has a GitHub-registered key on it — see
the `DEPLOY_KEY_PATH` variant in step 3.

```bash
gcloud compute scp ~/.ssh/mtile_deploy mtile-dev:~/mtile_deploy
gcloud compute ssh mtile-dev
```

## 3. Bootstrap (on the instance)

```bash
mkdir -p ~/.ssh && mv ~/mtile_deploy ~/.ssh/mtile_deploy && chmod 600 ~/.ssh/mtile_deploy
curl -fsSL https://raw.githubusercontent.com/amdson/MTile/main/scripts/vm-bootstrap.sh | bash
```

If the instance already has a private key whose pub side is registered with
GitHub (e.g. `~/.ssh/id_rsa`), point the script at it instead of copying one:

```bash
curl -fsSL https://raw.githubusercontent.com/amdson/MTile/main/scripts/vm-bootstrap.sh \
  | DEPLOY_KEY_PATH=$HOME/.ssh/id_rsa bash
```

The script sudo-elevates itself, installs the .NET 8 SDK, wires the deploy key
up for github.com (it looks for `~/.ssh/mtile_deploy` by default), clones the
repo to `~/MTile`, builds `MTile.Core`, and runs the test suite. To pick a
branch: `curl ... | GIT_BRANCH=my-branch bash`.

When done:

```bash
cd ~/MTile
dotnet build MTile.Core.csproj
dotnet test MTile.Tests/MTile.Tests.csproj
```

(`dotnet` lands on PATH via `/etc/profile.d/dotnet.sh` — if the shell you ran
the bootstrap in doesn't see it, `exec bash -l` or reconnect.)

## 4. Re-sync by hand

```bash
bash ~/MTile/scripts/vm-bootstrap.sh      # idempotent: ff-pulls + rebuilds
```

## 5. Stop / start / delete (save $)

```bash
gcloud compute instances stop mtile-dev     # halts compute billing (disk still charged)
gcloud compute instances start mtile-dev    # external IP changes on restart
gcloud compute instances delete mtile-dev   # destroy everything
```

`gcloud compute ssh mtile-dev` resolves the new IP automatically after a
restart, so the IP change doesn't matter. The checkout and SDK live on the boot
disk, so after stop/start you only need step 4 to pull latest — no re-bootstrap.

## Without the gcloud CLI

gcloud is only doing three conveniences above (SSH key management, IP lookup,
file copy). The web console + plain `ssh`/`scp` replace all three:

1. **SSH access (once):** Console → Compute Engine → VM instances → instance →
   Edit → **SSH Keys** → paste your `id_rsa.pub` → Save. The username at the
   end of the pasted line becomes your login user. (Adding it under
   Settings → **Metadata → SSH Keys** instead makes it project-wide.)
2. **Connect:** external IP is shown in the VM instances list.
   ```bash
   ssh -i ~/.ssh/id_rsa USER@EXTERNAL_IP
   ```
3. **GitHub key:** cleanest on a fresh box is to mint one on the instance so no
   private key ever travels — the script auto-detects it at that path:
   ```bash
   ssh-keygen -t ed25519 -f ~/.ssh/mtile_deploy -N "" -C "mtile-gcp-vm"
   cat ~/.ssh/mtile_deploy.pub   # → GitHub repo Settings → Deploy keys (read-only)
   curl -fsSL https://raw.githubusercontent.com/amdson/MTile/main/scripts/vm-bootstrap.sh | bash
   ```

Zero-local-tooling variant: the **SSH** button next to the instance in the
console opens a browser terminal (no key setup at all) — then step 3 is the
whole setup. Stop/start/delete are buttons in the same console list.

Caveat: only the `default` VPC network ships with an allow-SSH firewall rule;
on a custom network, add one for inbound TCP 22 first.

## Optional: bootstrap at boot instead

GCP's equivalent of EC2 user-data is `--metadata-from-file startup-script=...`.
Two differences from EC2: it runs on **every** boot (not just the first —
harmless here since the script is idempotent), and it runs as root, so
`TARGET_USER` must be set explicitly:

```bash
cat > /tmp/startup.sh <<EOF
#!/usr/bin/env bash
export DEPLOY_KEY_B64="$(base64 -w0 ~/.ssh/mtile_deploy)"
export TARGET_USER="$(whoami)"        # your GCP login user
export GIT_BRANCH="main"
curl -fsSL https://raw.githubusercontent.com/amdson/MTile/main/scripts/vm-bootstrap.sh | bash
EOF

gcloud compute instances create mtile-dev \
  --machine-type=e2-standard-2 \
  --image-family=ubuntu-2404-lts-amd64 --image-project=ubuntu-os-cloud \
  --boot-disk-size=30GB \
  --metadata-from-file startup-script=/tmp/startup.sh
```

Watch progress: `gcloud compute ssh mtile-dev` then
`sudo journalctl -u google-startup-scripts -f`. Caveat: `TARGET_USER` must
match the user GCP creates for your SSH login, which is derived from your
Google account — if unsure, SSH in once first and check `whoami`.
