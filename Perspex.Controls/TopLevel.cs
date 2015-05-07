﻿// -----------------------------------------------------------------------
// <copyright file="TopLevel.cs" company="Steven Kirk">
// Copyright 2014 MIT Licence. See licence.md for more information.
// </copyright>
// -----------------------------------------------------------------------

namespace Perspex.Controls
{
    using Perspex.Input;
    using Perspex.Input.Raw;
    using Perspex.Layout;
    using Perspex.Platform;
    using Perspex.Rendering;
    using Perspex.Styling;
    using Perspex.Threading;
    using Splat;
    using System;
    using System.Reactive.Disposables;
    using System.Reactive.Linq;

    /// <summary>
    /// Base class for top-level windows.
    /// </summary>
    /// <remarks>
    /// This class acts as a base for top level windows such as <see cref="Window"/> and 
    /// <see cref="PopupRoot"/>. It handles scheduling layout, styling and rendering as well as 
    /// tracking the window <see cref="ClientSize"/> and <see cref="IsActive"/> state.
    /// </remarks>
    public abstract class TopLevel : ContentControl, ILayoutRoot, IRenderRoot, ICloseable, IFocusScope
    {
        /// <summary>
        /// Defines the <see cref="ClientSize"/> property.
        /// </summary>
        public static readonly PerspexProperty<Size> ClientSizeProperty =
            PerspexProperty.Register<TopLevel, Size>("ClientSize");

        /// <summary>
        /// Defines the <see cref="IsActive"/> property.
        /// </summary>
        public static readonly PerspexProperty<bool> IsActiveProperty =
            PerspexProperty.Register<TopLevel, bool>("IsActive");

        /// <summary>
        /// The dispatcher for the window.
        /// </summary>
        private Dispatcher dispatcher;

        /// <summary>
        /// The render manager for the window.s
        /// </summary>
        private IRenderManager renderManager;

        /// <summary>
        /// The window renderer.
        /// </summary>
        private IRenderer renderer;

        /// <summary>
        /// The input manager for the window.
        /// </summary>
        private IInputManager inputManager;

        private bool autoSizing;

        /// <summary>
        /// Statically initializes the <see cref="TopLevel"/> class.
        /// </summary>
        static TopLevel()
        {
            TopLevel.AffectsMeasure(TopLevel.ClientSizeProperty);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TopLevel"/> class.
        /// </summary>
        /// <param name="impl">The platform-specific window implementation.</param>
        public TopLevel(ITopLevelImpl impl)
        {
            IPlatformRenderInterface renderInterface = Locator.Current.GetService<IPlatformRenderInterface>();

            this.PlatformImpl = impl;
            this.inputManager = Locator.Current.GetService<IInputManager>();
            this.LayoutManager = Locator.Current.GetService<ILayoutManager>();
            this.renderManager = Locator.Current.GetService<IRenderManager>();

            if (renderInterface == null)
            {
                throw new InvalidOperationException(
                    "Could not create an interface to the rendering subsystem: maybe no rendering subsystem was initialized?");
            }

            if (this.PlatformImpl == null)
            {
                throw new InvalidOperationException(
                    "Could not create window implementation: maybe no windowing subsystem was initialized?");
            }

            if (this.inputManager == null)
            {
                throw new InvalidOperationException(
                    "Could not create input manager: maybe Application.RegisterServices() wasn't called?");
            }

            if (this.LayoutManager == null)
            {
                throw new InvalidOperationException(
                    "Could not create layout manager: maybe Application.RegisterServices() wasn't called?");
            }

            if (this.renderManager == null)
            {
                throw new InvalidOperationException(
                    "Could not create render manager: maybe Application.RegisterServices() wasn't called?");
            }

            this.PlatformImpl.SetOwner(this);
            this.PlatformImpl.Activated = this.HandleActivated;
            this.PlatformImpl.Deactivated = this.HandleDeactivated;
            this.PlatformImpl.Closed = this.HandleClosed;
            this.PlatformImpl.Input = this.HandleInput;
            this.PlatformImpl.Paint = this.HandlePaint;
            this.PlatformImpl.Resized = this.HandleResized;
            
            Size clientSize = this.ClientSize = this.PlatformImpl.ClientSize;

            this.dispatcher = Dispatcher.UIThread;
            this.renderer = renderInterface.CreateRenderer(this.PlatformImpl.Handle, clientSize.Width, clientSize.Height);

            this.LayoutManager.Root = this;
            this.LayoutManager.LayoutNeeded.Subscribe(_ => this.HandleLayoutNeeded());
            this.renderManager.RenderNeeded.Subscribe(_ => this.HandleRenderNeeded());

            IStyler styler = Locator.Current.GetService<IStyler>();
            styler.ApplyStyles(this);

            this.GetObservable(ClientSizeProperty).Skip(1).Subscribe(x => this.PlatformImpl.ClientSize = x);
        }

        /// <summary>
        /// Fired when the window is activated.
        /// </summary>
        public event EventHandler Activated;

        /// <summary>
        /// Fired when the window is closed.
        /// </summary>
        public event EventHandler Closed;

        /// <summary>
        /// Fired when the window is deactivated.
        /// </summary>
        public event EventHandler Deactivated;

        /// <summary>
        /// Gets or sets the client size of the window.
        /// </summary>
        public Size ClientSize
        {
            get { return this.GetValue(ClientSizeProperty); }
            private set { this.SetValue(ClientSizeProperty, value); }
        }

        /// <summary>
        /// Gets a value that indicates whether the window is active.
        /// </summary>
        public bool IsActive
        {
            get { return this.GetValue(IsActiveProperty); }
            private set { this.SetValue(IsActiveProperty, value); }
        }

        /// <summary>
        /// Gets the layout manager for the window.
        /// </summary>
        public ILayoutManager LayoutManager
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the platform-specific window implementation.
        /// </summary>
        public ITopLevelImpl PlatformImpl
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the window renderer.
        /// </summary>
        IRenderer IRenderRoot.Renderer
        {
            get { return this.renderer; }
        }

        /// <summary>
        /// Gets the window render manager.
        /// </summary>
        IRenderManager IRenderRoot.RenderManager
        {
            get { return this.renderManager; }
        }

        /// <summary>
        /// Translates a point from window coordinates into screen coordinates.
        /// </summary>
        /// <param name="p">The point.</param>
        /// <returns>The point in screen coordinates.</returns>
        Point IRenderRoot.TranslatePointToScreen(Point p)
        {
            return this.PlatformImpl.PointToScreen(p);
        }

        /// <summary>
        /// Activates the window.
        /// </summary>
        public void Activate()
        {
            this.PlatformImpl.Activate();
        }

        protected IDisposable BeginAutoSizing()
        {
            this.autoSizing = true;
            return Disposable.Create(() => this.autoSizing = false);
        }

        /// <summary>
        /// Carries out the arrange pass of the window.
        /// </summary>
        /// <param name="finalSize">The final window size.</param>
        /// <returns>The <paramref name="finalSize"/> parameter unchanged.</returns>
        protected override Size ArrangeOverride(Size finalSize)
        {
            using (this.BeginAutoSizing())
            {
                this.PlatformImpl.ClientSize = finalSize;
            }

            return base.ArrangeOverride(finalSize);
        }

        /// <summary>
        /// Handles an activated notification from <see cref="ITopLevelImpl.Activated"/>.
        /// </summary>
        private void HandleActivated()
        {
            if (this.Activated != null)
            {
                this.Activated(this, EventArgs.Empty);
            }

            FocusManager.Instance.SetFocusScope(this);
            this.IsActive = true;
        }

        /// <summary>
        /// Handles a closed notification from <see cref="ITopLevelImpl.Closed"/>.
        /// </summary>
        private void HandleClosed()
        {
            if (this.Closed != null)
            {
                this.Closed(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Handles a deactivated notification from <see cref="ITopLevelImpl.Deactivated"/>.
        /// </summary>
        private void HandleDeactivated()
        {
            this.IsActive = false;

            if (this.Deactivated != null)
            {
                this.Deactivated(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Handles input from <see cref="ITopLevelImpl.Input"/>.
        /// </summary>
        private void HandleInput(RawInputEventArgs e)
        {
            this.inputManager.Process(e);
        }

        /// <summary>
        /// Handles a layout request from <see cref="LayoutManager.LayoutNeeded"/>.
        /// </summary>
        private void HandleLayoutNeeded()
        {
            this.dispatcher.InvokeAsync(LayoutManager.ExecuteLayoutPass, DispatcherPriority.Render);
        }

        /// <summary>
        /// Handles a render request from <see cref="RenderManager.RenderNeeded"/>.
        /// </summary>
        private void HandleRenderNeeded()
        {
            this.dispatcher.InvokeAsync(
                () => this.PlatformImpl.Invalidate(new Rect(this.ClientSize)), 
                DispatcherPriority.Render);
        }

        /// <summary>
        /// Handles a paint request from <see cref="ITopLevelImpl.Paint"/>.
        /// </summary>
        private void HandlePaint(Rect rect, IPlatformHandle handle)
        {
            this.renderer.Render(this, handle);
            this.renderManager.RenderFinished();
        }

        /// <summary>
        /// Handles a resize notification from <see cref="ITopLevelImpl.Resized"/>.
        /// </summary>
        private void HandleResized(Size clientSize)
        {
            if (!this.autoSizing)
            {
                this.Width = clientSize.Width;
                this.Height = clientSize.Height;
            }

            this.ClientSize = clientSize;
            this.renderer.Resize((int)clientSize.Width, (int)clientSize.Height);
            this.LayoutManager.ExecuteLayoutPass();
            this.PlatformImpl.Invalidate(new Rect(clientSize));
        }
    }
}
