using LmChat;

// Aus dem Finder gestartet fehlt der PATH der Shell – sonst fänden MCP-Plugins Programme wie npx nicht
var path = Environment.GetEnvironmentVariable("PATH") ?? "";
foreach (var dir in new[] { "/usr/local/bin", "/opt/homebrew/bin" })
    if (!path.Split(':').Contains(dir))
        path = dir + ":" + path;
Environment.SetEnvironmentVariable("PATH", path);

NSApplication.Init();
var app = NSApplication.SharedApplication;
app.Delegate = new AppDelegate();
app.Run();
