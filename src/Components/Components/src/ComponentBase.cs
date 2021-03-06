// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.AspNetCore.Components.RenderTree;
using System;
using System.Threading.Tasks;

namespace Microsoft.AspNetCore.Components
{
    // IMPORTANT
    //
    // Many of these names are used in code generation. Keep these in sync with the code generation code
    // See: src/Microsoft.AspNetCore.Components.Razor.Extensions/ComponentsApi.cs

    // Most of the developer-facing component lifecycle concepts are encapsulated in this
    // base class. The core components rendering system doesn't know about them (it only knows
    // about IComponent). This gives us flexibility to change the lifecycle concepts easily,
    // or for developers to design their own lifecycles as different base classes.

    // TODO: When the component lifecycle design stabilises, add proper unit tests for ComponentBase.

    /// <summary>
    /// Optional base class for components. Alternatively, components may
    /// implement <see cref="IComponent"/> directly.
    /// </summary>
    public abstract class ComponentBase : IComponent, IHandleEvent, IHandleAfterRender
    {
        /// <summary>
        /// Specifies the name of the <see cref="RenderTree"/>-building method.
        /// </summary>
        public const string BuildRenderTreeMethodName = nameof(BuildRenderTree);

        private readonly RenderFragment _renderFragment;
        private RenderHandle _renderHandle;
        private bool _hasCalledInit;
        private bool _hasNeverRendered = true;
        private bool _hasPendingQueuedRender;

        /// <summary>
        /// Constructs an instance of <see cref="ComponentBase"/>.
        /// </summary>
        public ComponentBase()
        {
            _renderFragment = BuildRenderTree;
        }

        /// <summary>
        /// Renders the component to the supplied <see cref="RenderTreeBuilder"/>.
        /// </summary>
        /// <param name="builder">A <see cref="RenderTreeBuilder"/> that will receive the render output.</param>
        protected virtual void BuildRenderTree(RenderTreeBuilder builder)
        {
            // Developers can either override this method in derived classes, or can use Razor
            // syntax to define a derived class and have the compiler generate the method.
            _hasPendingQueuedRender = false;
            _hasNeverRendered = false;
        }

        /// <summary>
        /// Method invoked when the component is ready to start, having received its
        /// initial parameters from its parent in the render tree.
        /// </summary>
        protected virtual void OnInit()
        {
        }

        /// <summary>
        /// Method invoked when the component is ready to start, having received its
        /// initial parameters from its parent in the render tree.
        /// 
        /// Override this method if you will perform an asynchronous operation and
        /// want the component to refresh when that operation is completed.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing any asynchronous operation.</returns>
        protected virtual Task OnInitAsync()
            => Task.CompletedTask;

        /// <summary>
        /// Method invoked when the component has received parameters from its parent in
        /// the render tree, and the incoming values have been assigned to properties.
        /// </summary>
        protected virtual void OnParametersSet()
        {
        }

        /// <summary>
        /// Method invoked when the component has received parameters from its parent in
        /// the render tree, and the incoming values have been assigned to properties.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing any asynchronous operation.</returns>
        protected virtual Task OnParametersSetAsync()
            => Task.CompletedTask;

        /// <summary>
        /// Notifies the component that its state has changed. When applicable, this will
        /// cause the component to be re-rendered.
        /// </summary>
        protected void StateHasChanged()
        {
            if (_hasPendingQueuedRender)
            {
                return;
            }

            if (_hasNeverRendered || ShouldRender())
            {
                _hasPendingQueuedRender = true;

                try
                {
                    _renderHandle.Render(_renderFragment);
                }
                catch
                {
                    _hasPendingQueuedRender = false;
                    throw;
                }
            }
        }

        /// <summary>
        /// Returns a flag to indicate whether the component should render.
        /// </summary>
        /// <returns></returns>
        protected virtual bool ShouldRender()
            => true;

        /// <summary>
        /// Method invoked after each time the component has been rendered.
        /// </summary>
        protected virtual void OnAfterRender()
        {
        }

        /// <summary>
        /// Method invoked after each time the component has been rendered. Note that the component does
        /// not automatically re-render after the completion of any returned <see cref="Task"/>, because
        /// that would cause an infinite render loop.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing any asynchronous operation.</returns>
        protected virtual Task OnAfterRenderAsync()
            => Task.CompletedTask;

        /// <summary>
        /// Executes the supplied work item on the associated renderer's
        /// synchronization context.
        /// </summary>
        /// <param name="workItem">The work item to execute.</param>
        protected Task Invoke(Action workItem)
            => _renderHandle.Invoke(workItem);

        /// <summary>
        /// Executes the supplied work item on the associated renderer's
        /// synchronization context.
        /// </summary>
        /// <param name="workItem">The work item to execute.</param>
        protected Task InvokeAsync(Func<Task> workItem)
            => _renderHandle.InvokeAsync(workItem);

        void IComponent.Configure(RenderHandle renderHandle)
        {
            // This implicitly means a ComponentBase can only be associated with a single
            // renderer. That's the only use case we have right now. If there was ever a need,
            // a component could hold a collection of render handles.
            if (_renderHandle.IsInitialized)
            {
                throw new InvalidOperationException($"The render handle is already set. Cannot initialize a {nameof(ComponentBase)} more than once.");
            }

            _renderHandle = renderHandle;
        }

        /// <summary>
        /// Method invoked to apply initial or updated parameters to the component.
        /// </summary>
        /// <param name="parameters">The parameters to apply.</param>
        public virtual Task SetParametersAsync(ParameterCollection parameters)
        {
            parameters.SetParameterProperties(this);
            if (!_hasCalledInit)
            {
                return RunInitAndSetParameters();
            }
            else
            {
                OnParametersSet();
                // If you override OnInitAsync or OnParametersSetAsync and return a noncompleted task,
                // then by default we automatically re-render once each of those tasks completes.
                var isAsync = false;
                Task parametersTask = null;
                (isAsync, parametersTask) = ProcessLifeCycletask(OnParametersSetAsync());
                StateHasChanged();
                // We call StateHasChanged here so that we render after OnParametersSet and after the
                // synchronous part of OnParametersSetAsync has run, and in case there is async work
                // we trigger another render.
                if (isAsync)
                {
                    return parametersTask;
                }

                return Task.CompletedTask;
            }
        }

        private async Task RunInitAndSetParameters()
        {
            _hasCalledInit = true;
            var initIsAsync = false;

            OnInit();
            Task initTask = null;
            (initIsAsync, initTask) = ProcessLifeCycletask(OnInitAsync());
            if (initIsAsync)
            {
                // Call state has changed here so that we render after the sync part of OnInitAsync has run
                // and wait for it to finish before we continue. If no async work has been done yet, we want
                // to defer calling StateHasChanged up until the first bit of async code happens or until
                // the end.
                StateHasChanged();
                await initTask;
            }

            OnParametersSet();
            Task parametersTask = null;
            var setParametersIsAsync = false;
            (setParametersIsAsync, parametersTask) = ProcessLifeCycletask(OnParametersSetAsync());
            // We always call StateHasChanged here as we want to trigger a rerender after OnParametersSet and
            // the synchronous part of OnParametersSetAsync has run, triggering another re-render in case there
            // is additional async work.
            StateHasChanged();
            if (setParametersIsAsync)
            {
                await parametersTask;
            }
        }

        private (bool isAsync, Task asyncTask) ProcessLifeCycletask(Task task)
        {
            if (task == null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            switch (task.Status)
            {
                // If it's already completed synchronously, no need to await and no
                // need to issue a further render (we already rerender synchronously).
                // Just need to make sure we propagate any errors.
                case TaskStatus.RanToCompletion:
                case TaskStatus.Canceled:
                    return (false, null);
                case TaskStatus.Faulted:
                    HandleException(task.Exception);
                    return (false, null);
                // For incomplete tasks, automatically re-render on successful completion
                default:
                    return (true, ReRenderAsyncTask(task));
            }
        }

        private async Task ReRenderAsyncTask(Task task)
        {
            try
            {
                await task;
                StateHasChanged();
            }
            catch (Exception ex)
            {
                // Either the task failed, or it was cancelled, or StateHasChanged threw.
                // We want to report task failure or StateHasChanged exceptions only.
                if (!task.IsCanceled)
                {
                    HandleException(ex);
                }
            }
        }

        private async void ContinueAfterLifecycleTask(Task task)
        {
            switch (task == null ? TaskStatus.RanToCompletion : task.Status)
            {
                // If it's already completed synchronously, no need to await and no
                // need to issue a further render (we already rerender synchronously).
                // Just need to make sure we propagate any errors.
                case TaskStatus.RanToCompletion:
                case TaskStatus.Canceled:
                    break;
                case TaskStatus.Faulted:
                    HandleException(task.Exception);
                    break;

                // For incomplete tasks, automatically re-render on successful completion
                default:
                    try
                    {
                        await task;
                        StateHasChanged();
                    }
                    catch (Exception ex)
                    {
                        // Either the task failed, or it was cancelled, or StateHasChanged threw.
                        // We want to report task failure or StateHasChanged exceptions only.
                        if (!task.IsCanceled)
                        {
                            HandleException(ex);
                        }
                    }

                    break;
            }
        }

        private static void HandleException(Exception ex)
        {
            if (ex is AggregateException && ex.InnerException != null)
            {
                ex = ex.InnerException; // It's more useful
            }

            // TODO: Need better global exception handling
            Console.Error.WriteLine($"[{ex.GetType().FullName}] {ex.Message}\n{ex.StackTrace}");
        }

        void IHandleEvent.HandleEvent(EventHandlerInvoker binding, UIEventArgs args)
        {
            var task = binding.Invoke(args);
            ContinueAfterLifecycleTask(task);

            // After each event, we synchronously re-render (unless !ShouldRender())
            // This just saves the developer the trouble of putting "StateHasChanged();"
            // at the end of every event callback.
            StateHasChanged();
        }

        void IHandleAfterRender.OnAfterRender()
        {
            OnAfterRender();

            var onAfterRenderTask = OnAfterRenderAsync();
            if (onAfterRenderTask != null && onAfterRenderTask.Status != TaskStatus.RanToCompletion)
            {
                // Note that we don't call StateHasChanged to trigger a render after
                // handling this, because that would be an infinite loop. The only
                // reason we have OnAfterRenderAsync is so that the developer doesn't
                // have to use "async void" and do their own exception handling in
                // the case where they want to start an async task.
                var taskWithHandledException = HandleAfterRenderException(onAfterRenderTask);
            }
        }

        private async Task HandleAfterRenderException(Task parentTask)
        {
            try
            {
                await parentTask;
            }
            catch (Exception e)
            {
                HandleException(e);
            }
        }
    }
}
