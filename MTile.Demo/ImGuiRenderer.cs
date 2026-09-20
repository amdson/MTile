using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace MTileDemo;

// Dear ImGui ↔ MonoGame (DesktopGL) backend, vendored: owns the ImGui context, feeds it
// input, and renders its draw lists through a BasicEffect with scissor clipping. The
// NuGet MonoGame-ImGui packages are one-file wrappers of exactly this, unmaintained —
// keeping it here means the editor's UI layer has no second-hand dependency.
//
// Frame shape (DemoGame drives it):
//   Update: BeginFrame(gt) → build the UI → EndFrame()   (EndFrame calls ImGui.Render)
//   Draw:   ... the canvas ... → RenderDrawData()
// Draw data stays valid until the next NewFrame, so building in Update lets every widget
// action mutate editor state on the normal update path, and lets the canvas read
// WantCaptureMouse/Keyboard before it handles a gesture.
internal sealed class ImGuiRenderer : IDisposable
{
    private readonly GraphicsDevice _gd;
    private BasicEffect _effect;
    private readonly RasterizerState _rasterizer = new()
    {
        CullMode = CullMode.None,
        DepthBias = 0,
        FillMode = FillMode.Solid,
        MultiSampleAntiAlias = false,
        ScissorTestEnable = true,
        SlopeScaleDepthBias = 0,
    };

    private byte[] _vboData = Array.Empty<byte>();
    private byte[] _iboData = Array.Empty<byte>();
    private VertexBuffer _vbo;
    private IndexBuffer  _ibo;
    private int _vboSize, _iboSize;

    private readonly Dictionary<IntPtr, Texture2D> _textures = new();
    private int _nextTextureId = 1;
    private IntPtr _fontTexture;
    private int _scrollValue;

    // ImDrawVert: float2 pos, float2 uv, uint32 rgba — bound as-is, no per-vertex conversion.
    private static readonly VertexDeclaration VertDecl = new(
        Marshal.SizeOf<ImDrawVert>(),
        new VertexElement(0,  VertexElementFormat.Vector2, VertexElementUsage.Position, 0),
        new VertexElement(8,  VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
        new VertexElement(16, VertexElementFormat.Color,   VertexElementUsage.Color, 0));

    // XNA key → ImGui key. Letters/digits carry the editor's shortcuts; the rest is what a
    // text field and ImGui's own navigation need.
    private static readonly (Keys Xna, ImGuiKey Im)[] KeyMap = BuildKeyMap();

    public ImGuiRenderer(Game game)
    {
        _gd = game.GraphicsDevice;
        ImGui.CreateContext();
        ImGui.StyleColorsDark();
        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.ConfigFlags  |= ImGuiConfigFlags.NavEnableKeyboard;
        game.Window.TextInput += (_, e) => ImGui.GetIO().AddInputCharacter(e.Character);
        _effect = new BasicEffect(_gd);
        RebuildFontAtlas();
    }

    public void BeginFrame(GameTime time)
    {
        var io = ImGui.GetIO();
        var pp = _gd.PresentationParameters;
        io.DisplaySize = new System.Numerics.Vector2(pp.BackBufferWidth, pp.BackBufferHeight);
        io.DisplayFramebufferScale = new System.Numerics.Vector2(1f, 1f);
        io.DeltaTime = MathF.Max((float)time.ElapsedGameTime.TotalSeconds, 1f / 1000f);

        var mouse = Mouse.GetState();
        var kb = Keyboard.GetState();
        io.AddMousePosEvent(mouse.X, mouse.Y);
        io.AddMouseButtonEvent(0, mouse.LeftButton   == ButtonState.Pressed);
        io.AddMouseButtonEvent(1, mouse.RightButton  == ButtonState.Pressed);
        io.AddMouseButtonEvent(2, mouse.MiddleButton == ButtonState.Pressed);
        int wheel = mouse.ScrollWheelValue - _scrollValue;
        _scrollValue = mouse.ScrollWheelValue;
        if (wheel != 0) io.AddMouseWheelEvent(0f, wheel / 120f);

        foreach (var (xna, im) in KeyMap) io.AddKeyEvent(im, kb.IsKeyDown(xna));
        io.AddKeyEvent(ImGuiKey.ModCtrl,  kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl));
        io.AddKeyEvent(ImGuiKey.ModShift, kb.IsKeyDown(Keys.LeftShift)   || kb.IsKeyDown(Keys.RightShift));
        io.AddKeyEvent(ImGuiKey.ModAlt,   kb.IsKeyDown(Keys.LeftAlt)     || kb.IsKeyDown(Keys.RightAlt));
        io.AddKeyEvent(ImGuiKey.ModSuper, kb.IsKeyDown(Keys.LeftWindows) || kb.IsKeyDown(Keys.RightWindows));

        ImGui.NewFrame();
    }

    public void EndFrame() => ImGui.Render();

    public unsafe void RebuildFontAtlas()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int w, out int h, out int bpp);
        var data = new byte[w * h * bpp];
        Marshal.Copy(pixels, data, 0, data.Length);
        var tex = new Texture2D(_gd, w, h, false, SurfaceFormat.Color);
        tex.SetData(data);
        if (_fontTexture != IntPtr.Zero && _textures.Remove(_fontTexture, out var old)) old.Dispose();
        _fontTexture = Bind(tex);
        io.Fonts.SetTexID(_fontTexture);
        io.Fonts.ClearTexData();
    }

    public IntPtr Bind(Texture2D texture)
    {
        var id = new IntPtr(_nextTextureId++);
        _textures.Add(id, texture);
        return id;
    }

    public void RenderDrawData()
    {
        var data = ImGui.GetDrawData();
        if (data.CmdListsCount == 0) return;

        var lastBlend    = _gd.BlendState;
        var lastDepth    = _gd.DepthStencilState;
        var lastRaster   = _gd.RasterizerState;
        var lastSampler  = _gd.SamplerStates[0];
        var lastScissor  = _gd.ScissorRectangle;

        _gd.BlendFactor       = Color.White;
        _gd.BlendState        = BlendState.NonPremultiplied;
        _gd.DepthStencilState = DepthStencilState.DepthRead;
        _gd.RasterizerState   = _rasterizer;
        _gd.SamplerStates[0]  = SamplerState.LinearClamp;

        data.ScaleClipRects(ImGui.GetIO().DisplayFramebufferScale);
        UploadBuffers(data);
        DrawCommandLists(data);

        _gd.BlendState        = lastBlend;
        _gd.DepthStencilState = lastDepth;
        _gd.RasterizerState   = lastRaster;
        _gd.SamplerStates[0]  = lastSampler;
        _gd.ScissorRectangle  = lastScissor;
    }

    private unsafe void UploadBuffers(ImDrawDataPtr data)
    {
        int vertSize = Marshal.SizeOf<ImDrawVert>();
        if (data.TotalVtxCount > _vboSize)
        {
            _vbo?.Dispose();
            _vboSize = (int)(data.TotalVtxCount * 1.5f);
            _vbo = new VertexBuffer(_gd, VertDecl, _vboSize, BufferUsage.None);
            _vboData = new byte[_vboSize * vertSize];
        }
        if (data.TotalIdxCount > _iboSize)
        {
            _ibo?.Dispose();
            _iboSize = (int)(data.TotalIdxCount * 1.5f);
            _ibo = new IndexBuffer(_gd, IndexElementSize.SixteenBits, _iboSize, BufferUsage.None);
            _iboData = new byte[_iboSize * sizeof(ushort)];
        }
        if (_vbo == null || _ibo == null) return;

        int vtxOffset = 0, idxOffset = 0;
        for (int n = 0; n < data.CmdListsCount; n++)
        {
            var list = data.CmdLists[n];
            fixed (void* vDst = &_vboData[vtxOffset * vertSize])
            fixed (void* iDst = &_iboData[idxOffset * sizeof(ushort)])
            {
                Buffer.MemoryCopy((void*)list.VtxBuffer.Data, vDst,
                                  _vboData.Length - vtxOffset * vertSize, list.VtxBuffer.Size * vertSize);
                Buffer.MemoryCopy((void*)list.IdxBuffer.Data, iDst,
                                  _iboData.Length - idxOffset * sizeof(ushort), list.IdxBuffer.Size * sizeof(ushort));
            }
            vtxOffset += list.VtxBuffer.Size;
            idxOffset += list.IdxBuffer.Size;
        }
        _vbo.SetData(_vboData, 0, data.TotalVtxCount * vertSize);
        _ibo.SetData(_iboData, 0, data.TotalIdxCount * sizeof(ushort));
    }

    private void DrawCommandLists(ImDrawDataPtr data)
    {
        _gd.SetVertexBuffer(_vbo);
        _gd.Indices = _ibo;

        var io = ImGui.GetIO();
        _effect.World      = Matrix.Identity;
        _effect.View       = Matrix.Identity;
        _effect.Projection = Matrix.CreateOrthographicOffCenter(0f, io.DisplaySize.X, io.DisplaySize.Y, 0f, -1f, 1f);
        _effect.TextureEnabled     = true;
        _effect.VertexColorEnabled = true;

        int vtxOffset = 0, idxOffset = 0;
        for (int n = 0; n < data.CmdListsCount; n++)
        {
            var list = data.CmdLists[n];
            for (int c = 0; c < list.CmdBuffer.Size; c++)
            {
                var cmd = list.CmdBuffer[c];
                if (cmd.ElemCount == 0) continue;
                if (!_textures.TryGetValue(cmd.TextureId, out var texture)) continue;

                _gd.ScissorRectangle = new Rectangle(
                    (int)cmd.ClipRect.X, (int)cmd.ClipRect.Y,
                    (int)(cmd.ClipRect.Z - cmd.ClipRect.X), (int)(cmd.ClipRect.W - cmd.ClipRect.Y));
                _effect.Texture = texture;
                foreach (var pass in _effect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    _gd.DrawIndexedPrimitives(PrimitiveType.TriangleList,
                        vtxOffset + (int)cmd.VtxOffset,
                        idxOffset + (int)cmd.IdxOffset,
                        (int)cmd.ElemCount / 3);
                }
            }
            vtxOffset += list.VtxBuffer.Size;
            idxOffset += list.IdxBuffer.Size;
        }
    }

    private static (Keys, ImGuiKey)[] BuildKeyMap()
    {
        var map = new List<(Keys, ImGuiKey)>
        {
            (Keys.Tab, ImGuiKey.Tab),
            (Keys.Left, ImGuiKey.LeftArrow), (Keys.Right, ImGuiKey.RightArrow),
            (Keys.Up, ImGuiKey.UpArrow), (Keys.Down, ImGuiKey.DownArrow),
            (Keys.PageUp, ImGuiKey.PageUp), (Keys.PageDown, ImGuiKey.PageDown),
            (Keys.Home, ImGuiKey.Home), (Keys.End, ImGuiKey.End),
            (Keys.Insert, ImGuiKey.Insert), (Keys.Delete, ImGuiKey.Delete),
            (Keys.Back, ImGuiKey.Backspace), (Keys.Space, ImGuiKey.Space),
            (Keys.Enter, ImGuiKey.Enter), (Keys.Escape, ImGuiKey.Escape),
            (Keys.OemMinus, ImGuiKey.Minus), (Keys.OemPlus, ImGuiKey.Equal),
            (Keys.OemOpenBrackets, ImGuiKey.LeftBracket), (Keys.OemCloseBrackets, ImGuiKey.RightBracket),
            (Keys.OemTilde, ImGuiKey.GraveAccent), (Keys.OemComma, ImGuiKey.Comma),
            (Keys.OemPeriod, ImGuiKey.Period), (Keys.OemQuestion, ImGuiKey.Slash),
        };
        for (int i = 0; i < 26; i++) map.Add(((Keys)((int)Keys.A + i), (ImGuiKey)((int)ImGuiKey.A + i)));
        for (int i = 0; i < 10; i++) map.Add(((Keys)((int)Keys.D0 + i), (ImGuiKey)((int)ImGuiKey._0 + i)));
        for (int i = 0; i < 12; i++) map.Add(((Keys)((int)Keys.F1 + i), (ImGuiKey)((int)ImGuiKey.F1 + i)));
        return map.ToArray();
    }

    public void Dispose()
    {
        foreach (var t in _textures.Values) t.Dispose();
        _textures.Clear();
        _vbo?.Dispose();
        _ibo?.Dispose();
        _effect?.Dispose();
        _rasterizer.Dispose();
    }
}
