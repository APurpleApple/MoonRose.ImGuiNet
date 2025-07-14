using ImGuiNET;
using MoonWorks.AsyncIO;
using MoonWorks.Graphics;
using MoonWorks.Input;
using SDL3;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static ImGuiNET.ImGuiRenderer;

namespace ImGuiNET
{
    public class ImGuiRenderer
    {
        private MoonWorks.Game Game;

        private GraphicsDevice GraphicsDevice;
        private GraphicsPipeline GraphicsPipeline;

        private DebugTextureStorage TextureStorage;
        private Texture FontTexture;

        private uint VertexCount = 0;
        private uint IndexCount = 0;
        TransferBuffer vertexTransferBuffer = null;
        TransferBuffer indexTransferBuffer = null;
        private MoonWorks.Graphics.Buffer vertexBuffer = null;
        private MoonWorks.Graphics.Buffer indexBuffer = null;
        private Sampler sampler;
        private KeyCode[] _allKeys = Enum.GetValues<KeyCode>();
        public ImGuiRenderer(MoonWorks.Game game)
        {
            GraphicsDevice = game.GraphicsDevice;
            Game = game;
            ImGui.CreateContext();

            SetupInputs();
            TextureStorage = new DebugTextureStorage();
            BuildGraphicPipeline();
            BuildFontAtlas();
        }

        private void SetupInputs()
        {
            Inputs.TextInput += c =>
            {
                if (c == '\t') { return; }
                ImGui.GetIO().AddInputCharacter(c);
            };
        }

        public void BeforeLayout(float dt)
        {
            ImGui.GetIO().DeltaTime = dt;

            UpdateInputs();

            ImGui.NewFrame();
        }

        public virtual nint BindTexture(Texture texture)
        {
            TextureStorage.Add(texture);

            return texture.Handle;
        }

        private void UpdateInputs()
        {
            var io = ImGui.GetIO();

            if (io.WantCaptureKeyboard)
            {
                Game.MainWindow.StartTextInput();
            }
            else if (!io.WantCaptureKeyboard)
            {
                Game.MainWindow.StopTextInput();
            }

            var mouse = Game.Inputs.Mouse;
            var keyboard = Game.Inputs.Keyboard;
            io.AddMousePosEvent(mouse.X, mouse.Y);
            io.AddMouseButtonEvent(0, mouse.LeftButton.IsDown);
            io.AddMouseButtonEvent(1, mouse.RightButton.IsDown);
            io.AddMouseButtonEvent(2, mouse.MiddleButton.IsDown);
            io.AddMouseButtonEvent(3, mouse.X1Button.IsDown);
            io.AddMouseButtonEvent(4, mouse.X2Button.IsDown);

            io.AddMouseWheelEvent(
                (mouse.Wheel),
                (mouse.Wheel));

            foreach (var key in _allKeys)
            {
                if (TryMapKeys(key, out ImGuiKey imguikey))
                {
                    io.AddKeyEvent(imguikey, keyboard.IsDown(key));
                }
            }

            io.DisplaySize = new System.Numerics.Vector2(Game.MainWindow.Width, Game.MainWindow.Height);
            io.DisplayFramebufferScale = new System.Numerics.Vector2(1f, 1f);
        }

        public void AfterLayout(CommandBuffer renderBuffer, CommandBuffer uploadBuffer, Texture target)
        {
            ImGui.Render();
            unsafe { RenderDrawData(renderBuffer, uploadBuffer, target, ImGui.GetDrawData()); }
        }

        private void RenderDrawData(CommandBuffer renderBuffer, CommandBuffer uploadBuffer, Texture target, ImDrawDataPtr drawDataPtr)
        {
            if (drawDataPtr.TotalVtxCount == 0) { return; }
            if (drawDataPtr.TotalIdxCount == 0) { return; }
            if (!drawDataPtr.Valid) { return; }
            UpdateImGuiBuffers(uploadBuffer, drawDataPtr);

            RenderCommandLists(renderBuffer, target, drawDataPtr, ImGui.GetIO());
        }

        private unsafe Shader LoadShaderFromManifest(string backend, string name, ShaderCreateInfo createInfo)
        {
            ShaderFormat shaderFormat;
            string extension;
            string entryPointName;
            switch (backend)
            {
                case "vulkan":
                    shaderFormat = ShaderFormat.SPIRV;
                    extension = "spv";
                    entryPointName = "main";
                    break;

                case "metal":
                    shaderFormat = ShaderFormat.MSL;
                    extension = "msl";
                    entryPointName = "main0";
                    break;

                case "direct3d11":
                    shaderFormat = ShaderFormat.DXBC;
                    extension = "dxbc";
                    entryPointName = "main";
                    break;

                case "direct3d12":
                    shaderFormat = ShaderFormat.DXIL;
                    extension = "dxil";
                    entryPointName = "main";
                    break;

                default:
                    throw new ArgumentException("This shouldn't happen!");
            }

            var createInfoWithFormat = createInfo with { Format = shaderFormat };
            var path = $"MoonRose.ImGuiNet.{name}.{extension}";
            var assembly = typeof(ImGuiRenderer).Assembly;
            using var stream = assembly.GetManifestResourceStream(path);

            var buffer = NativeMemory.Alloc((nuint)stream.Length);
            var span = new Span<byte>(buffer, (int)stream.Length);
            stream.ReadExactly(span);

            var result = Shader.Create(
                GraphicsDevice,
                span,
                entryPointName,
                createInfoWithFormat
            );

            NativeMemory.Free(buffer);

            return result;
        }

        private void BuildGraphicPipeline()
        {
            var backend = SDL.SDL_GetGPUDeviceDriver(GraphicsDevice.Handle);
            GraphicsPipeline?.Dispose();

            sampler = Sampler.Create(GraphicsDevice, SamplerCreateInfo.PointClamp);

            Shader vertexShader = LoadShaderFromManifest(
                backend,
                "ImGui.vert",
                new ShaderCreateInfo()
                {
                    Stage = ShaderStage.Vertex,
                    NumUniformBuffers = 1,
                }
                );
            Shader fragmentShader = LoadShaderFromManifest(
                backend,
                "ImGui.frag",
                new ShaderCreateInfo()
                {
                    Stage = ShaderStage.Fragment,
                    NumSamplers = 1,
                }
                );

            GraphicsPipeline = GraphicsPipeline.Create(
                GraphicsDevice,
                new GraphicsPipelineCreateInfo
                {
                    TargetInfo = new GraphicsPipelineTargetInfo
                    {
                        ColorTargetDescriptions = [
                            new ColorTargetDescription
                            {
                                Format = Game.MainWindow.SwapchainFormat,
                                BlendState = ColorTargetBlendState.NonPremultipliedAlphaBlend
                            }
                        ]
                    },
                    DepthStencilState = DepthStencilState.Disable,
                    VertexShader = vertexShader,
                    FragmentShader = fragmentShader,
                    VertexInputState = VertexInputState.CreateSingleBinding<Position2DTextureColorVertex>(),
                    PrimitiveType = PrimitiveType.TriangleList,
                    RasterizerState = RasterizerState.CW_CullNone,
                    MultisampleState = MultisampleState.None
                }
            );
        }

        private unsafe void BuildFontAtlas()
        {
            var textureUploader = new ResourceUploader(GraphicsDevice);

            var io = ImGui.GetIO();

            io.Fonts.GetTexDataAsRGBA32(
                out System.IntPtr pixelData,
                out int width,
                out int height,
                out int bytesPerPixel
            );

            var pixelSpan = new ReadOnlySpan<Color>((void*)pixelData, width * height);

            FontTexture = textureUploader.CreateTexture2D(
                pixelSpan,
                TextureFormat.R8G8B8A8Unorm,
                TextureUsageFlags.Sampler,
                (uint)width,
                (uint)height);

            textureUploader.Upload();
            textureUploader.Dispose();

            io.Fonts.SetTexID(FontTexture.Handle);
            io.Fonts.ClearTexData();

            TextureStorage.Add(FontTexture);
        }

        private void RenderCommandLists(CommandBuffer commandBuffer, Texture renderTexture, ImDrawDataPtr drawDataPtr, ImGuiIOPtr ioPtr)
        {
            var view = Matrix4x4.CreateLookAt(
                new Vector3(0, 0, 1),
                Vector3.Zero,
                Vector3.UnitY
            );

            var projection = Matrix4x4.CreateOrthographicOffCenter(
                0,
                480,
                270,
                0,
                0.01f,
                4000f
            );

            var viewProjectionMatrix = view * projection;

            var renderPass = commandBuffer.BeginRenderPass(
                new ColorTargetInfo(renderTexture, LoadOp.Load)
            );

            renderPass.BindGraphicsPipeline(GraphicsPipeline);

            commandBuffer.PushVertexUniformData(
                Matrix4x4.CreateOrthographicOffCenter(0, ioPtr.DisplaySize.X, ioPtr.DisplaySize.Y, 0, -1, 1)
            );

            renderPass.BindVertexBuffers(vertexBuffer);
            renderPass.BindIndexBuffer(indexBuffer, IndexElementSize.Sixteen);

            uint vertexOffset = 0;
            uint indexOffset = 0;

            for (int n = 0; n < drawDataPtr.CmdListsCount; n += 1)
            {
                var cmdList = drawDataPtr.CmdLists[n];

                for (int cmdIndex = 0; cmdIndex < cmdList.CmdBuffer.Size; cmdIndex += 1)
                {
                    var drawCmd = cmdList.CmdBuffer[cmdIndex];

                    Texture tex = TextureStorage.GetTexture(drawCmd.TextureId);
                    if (tex == null || tex.Handle == 0)
                    {
                        indexOffset += drawCmd.ElemCount;
                        continue;
                    }
                    renderPass.BindFragmentSamplers(
                        new TextureSamplerBinding(tex, sampler)
                    );

                    var topLeft = Vector2.Transform(new Vector2(drawCmd.ClipRect.X, drawCmd.ClipRect.Y), viewProjectionMatrix);
                    var bottomRight = Vector2.Transform(new Vector2(drawCmd.ClipRect.Z, drawCmd.ClipRect.W), viewProjectionMatrix);

                    var width = drawCmd.ClipRect.Z - (int)drawCmd.ClipRect.X;
                    var height = drawCmd.ClipRect.W - (int)drawCmd.ClipRect.Y;

                    if (width <= 0 || height <= 0)
                    {
                        continue;
                    }

                    renderPass.SetScissor(
                        new MoonWorks.Graphics.Rect(
                            (int)drawCmd.ClipRect.X,
                            (int)drawCmd.ClipRect.Y,
                            (int)width,
                            (int)height
                        )
                    );

                    renderPass.DrawIndexedPrimitives(
                        drawCmd.ElemCount,
                        1,
                        indexOffset,
                        (int)vertexOffset,
                        0
                    );

                    indexOffset += drawCmd.ElemCount;
                }

                vertexOffset += (uint)cmdList.VtxBuffer.Size;
            }

            commandBuffer.EndRenderPass(renderPass);
        }

        public struct Position2DTextureColorVertex : IVertexType
        {
            public Vector2 Position;
            public Vector2 TexCoord;
            public Color Color;

            public Position2DTextureColorVertex(
                Vector2 position,
                Vector2 texcoord,
                Color color
            )
            {
                Position = position;
                TexCoord = texcoord;
                Color = color;
            }

            public static VertexElementFormat[] Formats =>
            [
                VertexElementFormat.Float2,
                VertexElementFormat.Float2,
                VertexElementFormat.Ubyte4Norm
            ];

            public static uint[] Offsets =>
            [
                0,
                8,
                16
            ];
        }

        private unsafe void UpdateImGuiBuffers(CommandBuffer uploadBuffer, ImDrawDataPtr drawDataPtr)
        {
            if (drawDataPtr.TotalVtxCount > VertexCount)
            {
                vertexBuffer?.Dispose();
                vertexTransferBuffer?.Dispose();

                VertexCount = (uint)(drawDataPtr.TotalVtxCount * 1.5f);
                vertexBuffer = MoonWorks.Graphics.Buffer.Create<Position2DTextureColorVertex>(
                    GraphicsDevice,
                    BufferUsageFlags.Vertex,
                VertexCount
                );

                vertexTransferBuffer = TransferBuffer.Create<Position2DTextureColorVertex>(GraphicsDevice, TransferBufferUsage.Upload, VertexCount);
            }

            if (drawDataPtr.TotalIdxCount > IndexCount)
            {
                indexBuffer?.Dispose();
                indexTransferBuffer?.Dispose();

                IndexCount = (uint)(drawDataPtr.TotalIdxCount * 1.5f);
                indexBuffer = MoonWorks.Graphics.Buffer.Create<ushort>(
                    GraphicsDevice,
                    BufferUsageFlags.Index,
                    IndexCount
                );

                indexTransferBuffer = TransferBuffer.Create<ushort>(GraphicsDevice, TransferBufferUsage.Upload, IndexCount);
            }

            int vertexOffset = 0;
            int indexOffset = 0;

            indexTransferBuffer.Map(true);
            vertexTransferBuffer.Map(true);
            Span<Position2DTextureColorVertex> vertices = vertexTransferBuffer.MappedSpan<Position2DTextureColorVertex>();
            Span<ushort> indices = indexTransferBuffer.MappedSpan<ushort>();
            for (var n = 0; n < drawDataPtr.CmdListsCount; n += 1)
            {
                var cmdList = drawDataPtr.CmdLists[n];

                new ReadOnlySpan<Position2DTextureColorVertex>((void*)cmdList.VtxBuffer.Data, cmdList.VtxBuffer.Size).CopyTo(vertices.Slice(vertexOffset));
                new ReadOnlySpan<ushort>((void*)cmdList.IdxBuffer.Data, cmdList.IdxBuffer.Size).CopyTo(indices.Slice(indexOffset));

                vertexOffset += cmdList.VtxBuffer.Size;
                indexOffset += cmdList.IdxBuffer.Size;
            }
            vertexTransferBuffer.Unmap();
            indexTransferBuffer.Unmap();

            var copyPass = uploadBuffer.BeginCopyPass();
            copyPass.UploadToBuffer(vertexTransferBuffer, vertexBuffer, true);
            copyPass.UploadToBuffer(indexTransferBuffer, indexBuffer, true);
            uploadBuffer.EndCopyPass(copyPass);
        }
        private bool TryMapKeys(KeyCode key, out ImGuiKey imguikey)
        {
            if (key == KeyCode.Unknown)
            {
                imguikey = ImGuiKey.None;
                return true;
            }

            imguikey = key switch
            {
                KeyCode.Backspace => ImGuiKey.Backspace,
                KeyCode.Tab => ImGuiKey.Tab,
                KeyCode.KeypadEnter => ImGuiKey.Enter,
                KeyCode.CapsLock => ImGuiKey.CapsLock,
                KeyCode.Escape => ImGuiKey.Escape,
                KeyCode.Space => ImGuiKey.Space,
                KeyCode.PageUp => ImGuiKey.PageUp,
                KeyCode.PageDown => ImGuiKey.PageDown,
                KeyCode.End => ImGuiKey.End,
                KeyCode.Home => ImGuiKey.Home,
                KeyCode.Left => ImGuiKey.LeftArrow,
                KeyCode.Right => ImGuiKey.RightArrow,
                KeyCode.Up => ImGuiKey.UpArrow,
                KeyCode.Down => ImGuiKey.DownArrow,
                KeyCode.PrintScreen => ImGuiKey.PrintScreen,
                KeyCode.Insert => ImGuiKey.Insert,
                KeyCode.Delete => ImGuiKey.Delete,
                KeyCode.D0 => ImGuiKey._0,
                >= KeyCode.D1 and <= KeyCode.D9 => ImGuiKey._1 + (key - KeyCode.D1),
                >= KeyCode.A and <= KeyCode.Z => ImGuiKey.A + (key - KeyCode.A),
                KeyCode.Keypad0 => ImGuiKey.Keypad0,
                >= KeyCode.Keypad1 and <= KeyCode.Keypad9 => ImGuiKey.Keypad1 + (key - KeyCode.Keypad1),
                KeyCode.KeypadMultiply => ImGuiKey.KeypadMultiply,
                KeyCode.KeypadPlus => ImGuiKey.KeypadAdd,
                KeyCode.KeypadMinus => ImGuiKey.KeypadSubtract,
                KeyCode.KeypadPeriod => ImGuiKey.KeypadDecimal,
                KeyCode.KeypadDivide => ImGuiKey.KeypadDivide,
                >= KeyCode.F1 and <= KeyCode.F12 => ImGuiKey.F1 + (key - KeyCode.F1),
                KeyCode.NumLockClear => ImGuiKey.NumLock,
                KeyCode.ScrollLock => ImGuiKey.ScrollLock,
                KeyCode.LeftShift => ImGuiKey.ModShift,
                KeyCode.LeftControl => ImGuiKey.ModCtrl,
                KeyCode.LeftAlt => ImGuiKey.ModAlt,
                KeyCode.Semicolon => ImGuiKey.Semicolon,
                KeyCode.Equals => ImGuiKey.Equal,
                KeyCode.Comma => ImGuiKey.Comma,
                KeyCode.Minus => ImGuiKey.Minus,
                KeyCode.Period => ImGuiKey.Period,
                KeyCode.Slash => ImGuiKey.Slash,
                KeyCode.Grave => ImGuiKey.GraveAccent,
                KeyCode.LeftBracket => ImGuiKey.LeftBracket,
                KeyCode.RightBracket => ImGuiKey.RightBracket,
                KeyCode.Backslash => ImGuiKey.Backslash,
                KeyCode.Apostrophe => ImGuiKey.Apostrophe,
                _ => ImGuiKey.None,
            };

            return imguikey != ImGuiKey.None;
        }
        public class DebugTextureStorage
        {
            Dictionary<IntPtr, WeakReference<Texture>> PointerToTexture = new Dictionary<IntPtr, WeakReference<Texture>>();

            public IntPtr Add(Texture texture)
            {
                if (!PointerToTexture.ContainsKey(texture.Handle))
                {
                    PointerToTexture.Add(texture.Handle, new WeakReference<Texture>(texture));
                }
                else
                {
                    PointerToTexture[texture.Handle] = new WeakReference<Texture>(texture);
                }
                return texture.Handle;
            }

            public Texture GetTexture(IntPtr pointer)
            {
                if (!PointerToTexture.ContainsKey(pointer))
                {
                    return null;
                }

                var result = PointerToTexture[pointer];

                if (!result.TryGetTarget(out var texture))
                {
                    PointerToTexture.Remove(pointer);
                    return null;
                }

                return texture;
            }
        }
    }
}
