using System;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace VsMcp.Extension.Services
{
    /// <summary>
    /// Provides thread-safe access to VS services by marshalling calls to the UI thread.
    /// </summary>
    public class VsServiceAccessor
    {
        private static readonly TimeSpan DefaultUiThreadTimeout = TimeSpan.FromSeconds(10);

        private readonly AsyncPackage _package;
        private DTE2 _dte;

        public VsServiceAccessor(AsyncPackage package)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
        }

        public async Task<DTE2> GetDteAsync()
        {
            if (_dte == null)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _dte = (DTE2)await _package.GetServiceAsync(typeof(DTE));
            }
            return _dte;
        }

        /// <summary>
        /// Executes an action on the UI thread and returns the result.
        /// All EnvDTE calls must go through this method.
        /// Times out after 10 seconds to prevent indefinite hangs.
        /// </summary>
        public async Task<T> RunOnUIThreadAsync<T>(Func<T> func)
        {
            using (var cts = new CancellationTokenSource(DefaultUiThreadTimeout))
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cts.Token);
                return func();
            }
        }

        /// <summary>
        /// Executes an action on the UI thread.
        /// Times out after 10 seconds to prevent indefinite hangs.
        /// </summary>
        public async Task RunOnUIThreadAsync(Action action)
        {
            using (var cts = new CancellationTokenSource(DefaultUiThreadTimeout))
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cts.Token);
                action();
            }
        }

        /// <summary>
        /// Executes an async function, switching to UI thread first.
        /// Times out after 10 seconds to prevent indefinite hangs.
        /// </summary>
        public async Task<T> RunOnUIThreadAsync<T>(Func<Task<T>> func)
        {
            using (var cts = new CancellationTokenSource(DefaultUiThreadTimeout))
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cts.Token);
                return await func();
            }
        }

        public async Task<IVsOutputWindow> GetOutputWindowAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return (IVsOutputWindow)await _package.GetServiceAsync(typeof(SVsOutputWindow));
        }
    }
}
