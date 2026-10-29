using Autodesk.Revit.UI;

namespace WindFlow.Revit;

/// <summary>Thin exporter: one ribbon panel, two buttons. The building UI, solver and viewport now live in the
/// standalone WindFlow.App -- this add-in's only job is getting a BuildingModel out of Revit and into the app's
/// hands (a saved .windflow.json, or a direct hand-off via OpenInAppCommand).</summary>
public sealed class App : IExternalApplication
{
    public Result OnStartup(UIControlledApplication app)
    {
        try { app.CreateRibbonTab("WindFlow"); } catch (Autodesk.Revit.Exceptions.ArgumentException) { /* tab already exists */ }
        var panel = app.CreateRibbonPanel("WindFlow", "WindFlow");

        panel.AddItem(new PushButtonData("WindFlowExport", "Export to\nWindFlow", typeof(App).Assembly.Location, typeof(ExportCommand).FullName)
        {
            ToolTip = "Extract the whole building and save a .windflow.json file.",
        });
        panel.AddItem(new PushButtonData("WindFlowOpenInApp", "Open in\nWindFlow", typeof(App).Assembly.Location, typeof(OpenInAppCommand).FullName)
        {
            ToolTip = "Extract the whole building and open it in the WindFlow app.",
        });

        var liveLinkButton = (PushButton)panel.AddItem(new PushButtonData("WindFlowLiveLink", "Live link\n(off)", typeof(App).Assembly.Location, typeof(LiveLinkToggleCommand).FullName)
        {
            ToolTip = "Keep the WindFlow app's model in sync with this document over a named pipe (docs/LIVELINK.md).",
        });
        LiveLinkToggleCommand.LiveLinkButton = liveLinkButton;
        LiveLink.Initialize(app);

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication app)
    {
        LiveLink.Shutdown();
        return Result.Succeeded;
    }
}
