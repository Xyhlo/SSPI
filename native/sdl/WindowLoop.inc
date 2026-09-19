        // SSPI's software loop keeps input responsive while waiting for the next
        // frame. Native presentation still owns VSYNC and scanout buffer safety.
        public bool ClearBeforeDraw { get; set; } = true;

        protected virtual void OnFramePresented(double intervalMs, double updateMs, double drawMs, double presentMs) { }

        public void Run()
        {
            Quit = false;
            ulong frequency = Math.Max(1UL, SDL_GetPerformanceFrequency());
            ulong deadline = 0, remainder = 0, previousPresent = 0;
            uint previousFps = 0;
            while (!Quit)
            {
                ProcessEvents();
                if (Quit) break;

                uint fps = Math.Max(1u, Math.Min(240u, _FPS));
                if (fps != previousFps)
                {
                    previousFps = fps;
                    deadline = remainder = 0;
                }
                ulong now = SDL_GetPerformanceCounter();
                if (now < deadline)
                {
                    // Re-poll input at most four milliseconds later. Sleeping
                    // releases CPU time to downloads; this is not a spin loop.
                    ulong milliseconds = (deadline - now) * 1000 / frequency;
                    SDL_Delay((uint)Math.Max(1UL, Math.Min(4UL, milliseconds)));
                    continue;
                }

                uint frameTick = SDL_GetTicks();
                ulong frameStart = now;
                OnCycleBegin(frameTick, frameTick);
                if (Quit) break;
                ulong updateEnd = SDL_GetPerformanceCounter();
                if (!NeedsRedraw(frameTick))
                {
                    deadline = remainder = 0;
                    SDL_Delay(4);
                    continue;
                }

                int status;
                if (ClearBeforeDraw)
                {
                    status = SDL_SetRenderDrawColor(Renderer.Handler, ClearR, ClearG, ClearB, 0xFF);
                    if (status < 0) throw new SDLException();
                    status = SDL_RenderClear(Renderer.Handler);
                    if (status < 0) throw new SDLException();
                }
                OnBackground(frameTick);
                OnDraw(frameTick);
                ulong drawEnd = SDL_GetPerformanceCounter();

                status = SDL_UpdateWindowSurface(Handler);
                if (status < 0) throw new SDLException();
                ulong presented = SDL_GetPerformanceCounter();
                double scale = 1000.0 / frequency;
                OnFramePresented(previousPresent == 0 ? 0 : (presented - previousPresent) * scale,
                    (updateEnd - frameStart) * scale, (drawEnd - updateEnd) * scale,
                    (presented - drawEnd) * scale);
                previousPresent = presented;
                LastDrawTick = frameTick;
                Invalidated = false;

                ulong period = frequency / fps;
                remainder += frequency % fps;
                if (remainder >= fps) { period++; remainder -= fps; }
                if (deadline == 0) deadline = frameStart;
                deadline += Math.Max(1UL, period);
                // A slow frame skips missed deadlines rather than rendering a
                // backlog. Start useful work again without an extra full sleep.
                if (deadline < presented) { deadline = presented; remainder = 0; }
            }
        }
