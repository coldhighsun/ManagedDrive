using ManagedDrive.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// ManagedDriveHelper: a minimal LocalSystem Windows service whose sole job is to publish and
// remove global (\GLOBAL??) DOS-device symlinks on behalf of the user-mode app, making a
// WinFsp-mounted RAM disk visible across sessions. See the plan for why this must run as SYSTEM.

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "ManagedDriveHelper");

builder.Services.AddSingleton<GlobalMountManager>();
builder.Services.AddHostedService<HelperPipeService>();

builder.Build().Run();
