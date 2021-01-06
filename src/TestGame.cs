using System;
using SDL2;
using RefreshCS;
using System.IO;
using System.Runtime.InteropServices;

namespace RefreshCSTest
{
    [StructLayout(LayoutKind.Sequential)]
    struct Vertex
    {
        public float x, y, z;
        public float u, v;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RaymarchUniforms
    {
        public float time, padding;
        public float resolutionX, resolutionY;
    }

    public class TestGame : IDisposable
    {
        bool quit = false;

        double t = 0;
        double dt = 0.01;

        ulong currentTime = SDL.SDL_GetPerformanceCounter();
        double accumulator = 0;

        IntPtr RefreshDevice;
        IntPtr WindowHandle;

        Refresh.Rect renderArea;
        Refresh.Rect flip;

        /* shaders */
        IntPtr passthroughVertexShaderModule;
        IntPtr raymarchFragmentShaderModule;

        IntPtr woodTexture;
        IntPtr noiseTexture;

        IntPtr vertexBuffer;
        UInt64[] offsets;

        RaymarchUniforms raymarchUniforms;

        IntPtr mainRenderPass;
        IntPtr mainColorTargetTexture;
        Refresh.TextureSlice mainColorTargetTextureSlice;

        IntPtr mainColorTarget;
        IntPtr mainDepthStencilTarget;

        IntPtr mainFrameBuffer;
        IntPtr raymarchPipeline;

        IntPtr sampler;

        IntPtr[] sampleTextures = new IntPtr[2];
        IntPtr[] sampleSamplers = new IntPtr[2];

        Refresh.Color clearColor;
        Refresh.DepthStencilValue depthStencilClearValue;

        /* Functions */

        public uint[] ReadBytecode(FileInfo fileInfo)
        {
            byte[] data;
            int size;
            using (FileStream stream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read))
            {
                size = (int) stream.Length;
                data = new byte[size];
                stream.Read(data, 0, size);
            }

            uint[] uintData = new uint[size / 4];
            using (var memoryStream = new MemoryStream(data))
            {
                using (var reader = new BinaryReader(memoryStream))
                {
                    for (int i = 0; i < size / 4; i++)
                    {
                        uintData[i] = reader.ReadUInt32();
                    }
                }
            }

            return uintData;
        }

        public bool Initialize(uint windowWidth, uint windowHeight)
        {
            if (SDL.SDL_Init(SDL.SDL_INIT_VIDEO | SDL.SDL_INIT_TIMER | SDL.SDL_INIT_GAMECONTROLLER) < 0)
            {
                System.Console.WriteLine("Failed to initialize SDL!");
                return false;
            }

            WindowHandle = SDL.SDL_CreateWindow(
                "RefreshCSTest",
                SDL.SDL_WINDOWPOS_UNDEFINED,
                SDL.SDL_WINDOWPOS_UNDEFINED,
                (int)windowWidth,
                (int)windowHeight,
                SDL.SDL_WindowFlags.SDL_WINDOW_VULKAN
            );

            Refresh.PresentationParameters presentationParameters;
            presentationParameters.deviceWindowHandle = WindowHandle;
            presentationParameters.presentMode = Refresh.PresentMode.Mailbox;

            RefreshDevice = Refresh.Refresh_CreateDevice(ref presentationParameters, 1);

            renderArea.x = 0;
            renderArea.y = 0;
            renderArea.w = (int)windowWidth;
            renderArea.h = (int)windowHeight;

            flip.x = 0;
            flip.y = (int)windowHeight;
            flip.w = (int)windowWidth;
            flip.h = -(int)windowHeight;

            clearColor.r = 100;
            clearColor.g = 149;
            clearColor.b = 237;
            clearColor.a = 255;

            depthStencilClearValue.depth = 1;
            depthStencilClearValue.stencil = 0;

            /* load shaders */

            var passthroughVertBytecodeFile = new System.IO.FileInfo("passthrough_vert.spv");
            var raymarchFragBytecodeFile = new System.IO.FileInfo("hexagon_grid.spv");

            unsafe
            {
                fixed (uint* ptr = ReadBytecode(passthroughVertBytecodeFile))
                {
                    Refresh.ShaderModuleCreateInfo passthroughVertexShaderModuleCreateInfo;
                    passthroughVertexShaderModuleCreateInfo.codeSize = (UIntPtr) passthroughVertBytecodeFile.Length;
                    passthroughVertexShaderModuleCreateInfo.byteCode = (IntPtr)ptr;

                    passthroughVertexShaderModule = Refresh.Refresh_CreateShaderModule(RefreshDevice, ref passthroughVertexShaderModuleCreateInfo);
                }

                fixed (uint* ptr = ReadBytecode(raymarchFragBytecodeFile))
                {
                    Refresh.ShaderModuleCreateInfo raymarchFragmentShaderModuleCreateInfo;
                    raymarchFragmentShaderModuleCreateInfo.codeSize = (UIntPtr) raymarchFragBytecodeFile.Length;
                    raymarchFragmentShaderModuleCreateInfo.byteCode = (IntPtr) ptr;

                    raymarchFragmentShaderModule = Refresh.Refresh_CreateShaderModule(RefreshDevice, ref raymarchFragmentShaderModuleCreateInfo);
                }
            }

            /* load textures */

            IntPtr pixels = Refresh.Refresh_Image_Load("woodgrain.png", out var textureWidth, out var textureHeight, out var numChannels);
            woodTexture = Refresh.Refresh_CreateTexture2D(
                RefreshDevice,
                Refresh.ColorFormat.R8G8B8A8,
                (uint)textureWidth,
                (uint)textureHeight,
                1,
                (uint)Refresh.TextureUsageFlagBits.SamplerBit
            );

            Refresh.Refresh_Image_Free(pixels);

            pixels = Refresh.Refresh_Image_Load("noise.png", out textureWidth, out textureHeight, out numChannels);
            noiseTexture = Refresh.Refresh_CreateTexture2D(
                RefreshDevice,
                Refresh.ColorFormat.R8G8B8A8,
                (uint)textureWidth,
                (uint)textureHeight,
                1,
                (uint)Refresh.TextureUsageFlagBits.SamplerBit
            );

            Refresh.Refresh_Image_Free(pixels);

            /* vertex data */

            var vertices = new Vertex[3];
            vertices[0].x = -1;
            vertices[0].y = -1;
            vertices[0].z = 0;
            vertices[0].u = 0;
            vertices[0].v = 1;

            vertices[1].x = 3;
            vertices[1].y = -1;
            vertices[1].z = 0;
            vertices[1].u = 1;
            vertices[1].v = 1;

            vertices[2].x = -1;
            vertices[2].y = 3;
            vertices[2].z = 0;
            vertices[2].u = 0;
            vertices[2].v = 0;

            vertexBuffer = Refresh.Refresh_CreateBuffer(
                RefreshDevice,
                (uint)Refresh.BufferUsageFlagBits.Vertex,
                5 * 3
            );

            GCHandle handle = GCHandle.Alloc(vertices, GCHandleType.Pinned);

            Refresh.Refresh_SetBufferData(
                RefreshDevice,
                vertexBuffer,
                0,
                handle.AddrOfPinnedObject(),
                5 * 3
            );

            handle.Free();

            offsets = new UInt64[1];
            offsets[0] = 0;

            /* uniforms */

            raymarchUniforms.time = 0;
            raymarchUniforms.padding = 0;
            raymarchUniforms.resolutionX = (float)windowWidth;
            raymarchUniforms.resolutionY = (float)windowHeight;

            /* render pass */

            Refresh.ColorTargetDescription mainColorTargetDescriptions;
            mainColorTargetDescriptions.format = Refresh.ColorFormat.R8G8B8A8;
            mainColorTargetDescriptions.loadOp = Refresh.LoadOp.Clear;
            mainColorTargetDescriptions.storeOp = Refresh.StoreOp.Store;
            mainColorTargetDescriptions.multisampleCount = Refresh.SampleCount.One;

            Refresh.DepthStencilTargetDescription mainDepthStencilTargetDescription;
            mainDepthStencilTargetDescription.depthFormat = Refresh.DepthFormat.Depth32Stencil8;
            mainDepthStencilTargetDescription.loadOp = Refresh.LoadOp.Clear;
            mainDepthStencilTargetDescription.storeOp = Refresh.StoreOp.DontCare;
            mainDepthStencilTargetDescription.stencilLoadOp = Refresh.LoadOp.DontCare;
            mainDepthStencilTargetDescription.stencilStoreOp = Refresh.StoreOp.DontCare;

            GCHandle colorTargetDescriptionHandle = GCHandle.Alloc(mainColorTargetDescriptions, GCHandleType.Pinned);
            GCHandle depthStencilTargetDescriptionHandle = GCHandle.Alloc(mainDepthStencilTargetDescription, GCHandleType.Pinned);

            Refresh.RenderPassCreateInfo mainRenderPassCreateInfo;
            mainRenderPassCreateInfo.colorTargetCount = 1;
            mainRenderPassCreateInfo.colorTargetDescriptions = colorTargetDescriptionHandle.AddrOfPinnedObject();
            mainRenderPassCreateInfo.depthStencilTargetDescription = depthStencilTargetDescriptionHandle.AddrOfPinnedObject();

            mainRenderPass = Refresh.Refresh_CreateRenderPass(RefreshDevice, ref mainRenderPassCreateInfo);

            colorTargetDescriptionHandle.Free();
            depthStencilTargetDescriptionHandle.Free();

            mainColorTargetTexture = Refresh.Refresh_CreateTexture2D(
                RefreshDevice,
                Refresh.ColorFormat.R8G8B8A8,
                windowWidth,
                windowHeight,
                1,
                (uint)Refresh.TextureUsageFlagBits.ColorTargetBit
            );

            mainColorTargetTextureSlice.texture = mainColorTargetTexture;
            mainColorTargetTextureSlice.rectangle.x = 0;
            mainColorTargetTextureSlice.rectangle.y = 0;
            mainColorTargetTextureSlice.rectangle.w = (int)windowWidth;
            mainColorTargetTextureSlice.rectangle.h = (int)windowHeight;
            mainColorTargetTextureSlice.depth = 0;
            mainColorTargetTextureSlice.layer = 0;
            mainColorTargetTextureSlice.level = 0;

            mainColorTarget = Refresh.Refresh_CreateColorTarget(
                RefreshDevice,
                Refresh.SampleCount.One,
                ref mainColorTargetTextureSlice
            );

            mainDepthStencilTarget = Refresh.Refresh_CreateDepthStencilTarget(
                RefreshDevice,
                windowWidth,
                windowHeight,
                Refresh.DepthFormat.Depth32Stencil8
            );

            IntPtr[] colorTargets = new IntPtr[] { mainColorTarget };

            GCHandle colorTargetHandle = GCHandle.Alloc(colorTargets, GCHandleType.Pinned);

            Refresh.FramebufferCreateInfo framebufferCreateInfo;
            framebufferCreateInfo.width = windowWidth;
            framebufferCreateInfo.height = windowHeight;
            framebufferCreateInfo.colorTargetCount = 1;
            framebufferCreateInfo.pColorTargets = colorTargetHandle.AddrOfPinnedObject();
            framebufferCreateInfo.depthStencilTarget = mainDepthStencilTarget;
            framebufferCreateInfo.renderPass = mainRenderPass;

            mainFrameBuffer = Refresh.Refresh_CreateFramebuffer(RefreshDevice, ref framebufferCreateInfo);

            colorTargetHandle.Free();

            System.Console.WriteLine("created framebuffer");

            /* pipeline */

            Refresh.ColorTargetBlendState[] colorTargetBlendStates = new Refresh.ColorTargetBlendState[1];
            colorTargetBlendStates[0].blendEnable = 0;
            colorTargetBlendStates[0].alphaBlendOp = 0;
            colorTargetBlendStates[0].colorBlendOp = 0;
            colorTargetBlendStates[0].colorWriteMask = (uint)(
                Refresh.ColorComponentFlagBits.R |
                Refresh.ColorComponentFlagBits.G |
                Refresh.ColorComponentFlagBits.B |
                Refresh.ColorComponentFlagBits.A
            );
            colorTargetBlendStates[0].destinationAlphaBlendFactor = 0;
            colorTargetBlendStates[0].destinationColorBlendFactor = 0;
            colorTargetBlendStates[0].sourceAlphaBlendFactor = 0;
            colorTargetBlendStates[0].sourceColorBlendFactor = 0;

            GCHandle colorTargetBlendStateHandle = GCHandle.Alloc(colorTargetBlendStates, GCHandleType.Pinned);

            float[] blendConstants = new float[] { 0, 0, 0, 0 };

            GCHandle blendConstantsHandle = GCHandle.Alloc(blendConstants, GCHandleType.Pinned);

            Refresh.ColorBlendState colorBlendState;
            colorBlendState.logicOpEnable = 0;
            colorBlendState.logicOp = Refresh.LogicOp.NoOp;
            colorBlendState.blendConstants = blendConstantsHandle.AddrOfPinnedObject();
            colorBlendState.blendStateCount = 1;
            colorBlendState.blendStates = colorTargetBlendStateHandle.AddrOfPinnedObject();

            Refresh.DepthStencilState depthStencilState;
            depthStencilState.depthTestEnable = 0;
            depthStencilState.backStencilState.compareMask = 0;
            depthStencilState.backStencilState.compareOp = Refresh.CompareOp.Never;
            depthStencilState.backStencilState.depthFailOp = Refresh.StencilOp.Zero;
            depthStencilState.backStencilState.failOp = Refresh.StencilOp.Zero;
            depthStencilState.backStencilState.passOp = Refresh.StencilOp.Zero;
            depthStencilState.backStencilState.reference = 0;
            depthStencilState.backStencilState.writeMask = 0;
            depthStencilState.compareOp = Refresh.CompareOp.Never;
            depthStencilState.depthBoundsTestEnable = 0;
            depthStencilState.depthWriteEnable = 0;
            depthStencilState.frontStencilState.compareMask = 0;
            depthStencilState.frontStencilState.compareOp = Refresh.CompareOp.Never;
            depthStencilState.frontStencilState.depthFailOp = Refresh.StencilOp.Zero;
            depthStencilState.frontStencilState.failOp = Refresh.StencilOp.Zero;
            depthStencilState.frontStencilState.passOp = Refresh.StencilOp.Zero;
            depthStencilState.frontStencilState.reference = 0;
            depthStencilState.frontStencilState.writeMask = 0;
            depthStencilState.maxDepthBounds = 1.0f;
            depthStencilState.minDepthBounds = 0.0f;
            depthStencilState.stencilTestEnable = 0;

            Refresh.ShaderStageState vertexShaderState;
            vertexShaderState.shaderModule = passthroughVertexShaderModule;
            vertexShaderState.entryPointName = "main";
            vertexShaderState.uniformBufferSize = 0;

            Refresh.ShaderStageState fragmentShaderStage;
            fragmentShaderStage.shaderModule = raymarchFragmentShaderModule;
            fragmentShaderStage.entryPointName = "main";
            fragmentShaderStage.uniformBufferSize = 4;

            Refresh.MultisampleState multisampleState;
            multisampleState.multisampleCount = Refresh.SampleCount.One;
            multisampleState.sampleMask = 0;

            Refresh.GraphicsPipelineLayoutCreateInfo pipelineLayoutCreateInfo;
            pipelineLayoutCreateInfo.vertexSamplerBindingCount = 0;
            pipelineLayoutCreateInfo.fragmentSamplerBindingCount = 2;

            Refresh.RasterizerState rasterizerState;
            rasterizerState.cullMode = Refresh.CullMode.Back;
            rasterizerState.depthBiasClamp = 0;
            rasterizerState.depthBiasConstantFactor = 0;
            rasterizerState.depthBiasEnable = 0;
            rasterizerState.depthBiasSlopeFactor = 0;
            rasterizerState.depthClampEnable = 0;
            rasterizerState.fillMode = Refresh.FillMode.Fill;
            rasterizerState.frontFace = Refresh.FrontFace.Clockwise;
            rasterizerState.lineWidth = 1.0f;

            Refresh.TopologyState topologyState;
            topologyState.topology = Refresh.PrimitiveType.TriangleList;

            Refresh.VertexBinding[] vertexBindings = new Refresh.VertexBinding[1];
            vertexBindings[0].binding = 0;
            vertexBindings[0].inputRate = Refresh.VertexInputRate.Vertex;
            vertexBindings[0].stride = 5;

            Refresh.VertexAttribute[] vertexAttributes = new Refresh.VertexAttribute[2];
            vertexAttributes[0].binding = 0;
            vertexAttributes[0].location = 0;
            vertexAttributes[0].format = Refresh.VertexElementFormat.Vector3;
            vertexAttributes[0].offset = 0;

            vertexAttributes[1].binding = 0;
            vertexAttributes[1].location = 1;
            vertexAttributes[1].format = Refresh.VertexElementFormat.Vector2;
            vertexAttributes[1].offset = 3;

            GCHandle vertexBindingsHandle = GCHandle.Alloc(vertexBindings, GCHandleType.Pinned);
            GCHandle vertexAttributesHandle = GCHandle.Alloc(vertexAttributes, GCHandleType.Pinned);

            Refresh.VertexInputState vertexInputState;
            vertexInputState.vertexBindings = vertexBindingsHandle.AddrOfPinnedObject();
            vertexInputState.vertexBindingCount = 1;
            vertexInputState.vertexAttributes = vertexAttributesHandle.AddrOfPinnedObject();
            vertexInputState.vertexAttributeCount = 2;

            Refresh.Viewport viewport;
            viewport.x = 0;
            viewport.y = 0;
            viewport.w = (float)windowWidth;
            viewport.h = (float)windowHeight;
            viewport.minDepth = 0;
            viewport.maxDepth = 1;

            GCHandle viewportHandle = GCHandle.Alloc(viewport, GCHandleType.Pinned);
            GCHandle scissorHandle = GCHandle.Alloc(renderArea, GCHandleType.Pinned);

            Refresh.ViewportState viewportState;
            viewportState.viewports = viewportHandle.AddrOfPinnedObject();
            viewportState.viewportCount = 1;
            viewportState.scissors = scissorHandle.AddrOfPinnedObject();
            viewportState.scissorCount = 1;

            Refresh.GraphicsPipelineCreateInfo graphicsPipelineCreateInfo;
            graphicsPipelineCreateInfo.colorBlendState = colorBlendState;
            graphicsPipelineCreateInfo.depthStencilState = depthStencilState;
            graphicsPipelineCreateInfo.vertexShaderState = vertexShaderState;
            graphicsPipelineCreateInfo.fragmentShaderStage = fragmentShaderStage;
            graphicsPipelineCreateInfo.multisampleState = multisampleState;
            graphicsPipelineCreateInfo.pipelineLayoutCreateInfo = pipelineLayoutCreateInfo;
            graphicsPipelineCreateInfo.rasterizerState = rasterizerState;
            graphicsPipelineCreateInfo.topologyState = topologyState;
            graphicsPipelineCreateInfo.vertexInputState = vertexInputState;
            graphicsPipelineCreateInfo.viewportState = viewportState;
            graphicsPipelineCreateInfo.renderPass = mainRenderPass;

            System.Console.WriteLine("creating graphics pipeline");

            raymarchPipeline = Refresh.Refresh_CreateGraphicsPipeline(RefreshDevice, ref graphicsPipelineCreateInfo);

            System.Console.WriteLine("created graphics pipeline");

            blendConstantsHandle.Free();
            colorTargetBlendStateHandle.Free();
            vertexBindingsHandle.Free();
            vertexAttributesHandle.Free();
            viewportHandle.Free();
            scissorHandle.Free();

            Refresh.SamplerStateCreateInfo samplerStateCreateInfo;
            samplerStateCreateInfo.addressModeU = Refresh.SamplerAddressMode.Repeat;
            samplerStateCreateInfo.addressModeV = Refresh.SamplerAddressMode.Repeat;
            samplerStateCreateInfo.addressModeW = Refresh.SamplerAddressMode.Repeat;
            samplerStateCreateInfo.anisotropyEnable = 0;
            samplerStateCreateInfo.borderColor = Refresh.BorderColor.IntOpaqueBlack;
            samplerStateCreateInfo.compareEnable = 0;
            samplerStateCreateInfo.compareOp = Refresh.CompareOp.Never;
            samplerStateCreateInfo.magFilter = Refresh.Filter.Linear;
            samplerStateCreateInfo.maxAnisotropy = 0;
            samplerStateCreateInfo.maxLod = 1;
            samplerStateCreateInfo.minFilter = Refresh.Filter.Linear;
            samplerStateCreateInfo.minLod = 1;
            samplerStateCreateInfo.mipLodBias = 1;
            samplerStateCreateInfo.mipmapMode = Refresh.SamplerMipmapMode.Linear;

            sampler = Refresh.Refresh_CreateSampler(RefreshDevice, ref samplerStateCreateInfo);

            sampleTextures[0] = woodTexture;
            sampleTextures[1] = noiseTexture;

            sampleSamplers[0] = sampler;
            sampleSamplers[1] = sampler;


            return true;
        }

        public void Run()
        {
            while (!quit)
            {
                SDL.SDL_Event _Event;

                while (SDL.SDL_PollEvent(out _Event) == 1)
                {
                    switch (_Event.type)
                    {
                        case SDL.SDL_EventType.SDL_QUIT:
                            quit = true;
                            break;
                    }
                }

                var newTime = SDL.SDL_GetPerformanceCounter();
                double frameTime = (newTime - currentTime) / (double)SDL.SDL_GetPerformanceFrequency();

                if (frameTime > 0.25)
                {
                    frameTime = 0.25;
                }

                currentTime = newTime;

                accumulator += frameTime;

                bool updateThisLoop = (accumulator >= dt);

                while (accumulator >= dt && !quit)
                {
                    Update(dt);

                    t += dt;
                    accumulator -= dt;
                }

                if (updateThisLoop && !quit)
                {
                    Draw();
                }
            }
        }

        public void Update(double dt)
        {
            raymarchUniforms.time = (float)t;
        }

        public void Draw()
        {
            IntPtr commandBuffer = Refresh.Refresh_AcquireCommandBuffer(RefreshDevice, 0);

            unsafe
            {
                fixed (Refresh.Color* ptr = &clearColor)
                {
                    Refresh.Refresh_BeginRenderPass(
                        RefreshDevice,
                        commandBuffer,
                        mainRenderPass,
                        mainFrameBuffer,
                        renderArea,
                        (IntPtr)ptr,
                        1,
                        ref depthStencilClearValue
                    );
                }
            }

            Refresh.Refresh_BindGraphicsPipeline(
                RefreshDevice,
                commandBuffer,
                raymarchPipeline
            );

            uint fragmentParamOffset;

            unsafe
            {
                fixed (RaymarchUniforms* ptr = &raymarchUniforms)
                {
                    fragmentParamOffset = Refresh.Refresh_PushFragmentShaderParams(
                        RefreshDevice,
                        commandBuffer,
                        (IntPtr)ptr,
                        1
                    );
                }
            }

            IntPtr[] vertexBuffers = new IntPtr[1];
            vertexBuffers[0] = vertexBuffer;

            Refresh.Refresh_BindVertexBuffers(
                RefreshDevice,
                commandBuffer,
                0,
                1,
                vertexBuffers,
                offsets
            );

            Refresh.Refresh_BindFragmentSamplers(
                RefreshDevice,
                commandBuffer,
                sampleTextures,
                sampleSamplers
            );

            Refresh.Refresh_DrawPrimitives(
                RefreshDevice,
                commandBuffer,
                0,
                1,
                0,
                fragmentParamOffset
            );

            Refresh.Refresh_QueuePresent(
                RefreshDevice,
                commandBuffer,
                ref mainColorTargetTextureSlice,
                ref flip,
                Refresh.Filter.Nearest
            );

            IntPtr[] commandBuffers = new IntPtr[1];
            commandBuffers[0] = commandBuffer;

            Refresh.Refresh_Submit(
                RefreshDevice,
                1,
                commandBuffers
            );
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }
    }
}
