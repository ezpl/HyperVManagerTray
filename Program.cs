using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using HyperVManagerTray.Helpers;

namespace HyperVManagerTray;

// Custom entry point — replaces the XAML-generated Main (suppressed via
// DISABLE_XAML_GENERATED_MAIN in the csproj) so startup failures are caught
// and shown to the user rather than silently terminating the process.
class Program
{
    [STAThread]
    static void Main()
    {
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
        }
        catch (Exception ex)
        {
            NativeMethods.Error(
                $"Hyper-V Manager Tray failed to start:\n\n{ex.Message}",
                "Hyper-V Manager Tray");
        }
    }
}
