// -----------------------------------------------------------------------
// <copyright file="Startup.cs" company="Petabridge, LLC">
//      Copyright (C) 2015 - 2020 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Net;
using System.Reflection;
using Akka.Actor;
using Akka.Bootstrap.Docker;
using Akka.Configuration;
using Akka.Hosting;
using Akka.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Petabridge.Cmd.Cluster;
using Petabridge.Cmd.Host;
using Petabridge.Cmd.Remote;
using Phobos.Actor;
using Phobos.Hosting;

namespace Petabridge.Phobos.Web
{
    public class Startup
    {

        public const string AppOtelSourceName = "MyApp";
        public static readonly ActivitySource MyTracer = new ActivitySource(AppOtelSourceName);
        public static readonly Meter MyMeter = new Meter(AppOtelSourceName);

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            // needed on .NET Core 3.1
            // see https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/README.md#special-case-when-using-insecure-channel
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport",
                true);
            
            var otelAgentAddress = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
            if (string.IsNullOrEmpty(otelAgentAddress))
            {
                // default local address
                otelAgentAddress = "http://0.0.0.0:4317";
            }
            
            var resource = ResourceBuilder.CreateDefault()
                .AddService(Assembly.GetEntryAssembly()!.GetName().Name, serviceVersion:Assembly.GetEntryAssembly().GetName().Version.ToString(), serviceInstanceId:$"{Dns.GetHostName()}");

            services.AddOpenTelemetryTracing(tracer =>
            {
                tracer.AddAspNetCoreInstrumentation()
                    .SetResourceBuilder(resource)
                    .AddSource(AppOtelSourceName)
                    .AddHttpClientInstrumentation()
                    .AddPhobosInstrumentation()
                    .AddOtlpExporter(options =>
                    {
                        options.Protocol = OtlpExportProtocol.Grpc;
                        options.Endpoint = new Uri(otelAgentAddress);
                    });
            });

            services.AddOpenTelemetryMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .SetResourceBuilder(resource)
                    .AddMeter(AppOtelSourceName)
                    .AddHttpClientInstrumentation()
                    .AddPhobosInstrumentation()
                    .AddOtlpExporter(options =>
                    {
                        options.Protocol = OtlpExportProtocol.Grpc;
                        options.Endpoint = new Uri(otelAgentAddress);
                    });
            });
            

            // sets up Akka.NET
            ConfigureAkka(services);
        }
        
        public static void ConfigureAkka(IServiceCollection services)
        {
            var config = ConfigurationFactory.ParseString(File.ReadAllText("app.conf")).BootstrapFromDocker();

            services.AddAkka("ClusterSys", (builder, provider) =>
            {
                builder
                    .AddHocon(config)
                    .WithPhobos(AkkaRunMode.AkkaCluster)
                    .StartActors((system, registry) =>
                    {
                        var consoleActor = system.ActorOf(Props.Create(() => new ConsoleActor()), "console");
                        var routerActor = system.ActorOf(Props.Empty.WithRouter(FromConfig.Instance), "echo");
                        var routerForwarder =
                            system.ActorOf(Props.Create(() => new RouterForwarderActor(routerActor)), "fwd");
                        registry.TryRegister<RouterForwarderActor>(routerForwarder);
                    })
                    .StartActors((system, registry) =>
                    {
                        // start https://cmd.petabridge.com/ for diagnostics and profit
                        var pbm = PetabridgeCmd.Get(system); // start Pbm
                        pbm.RegisterCommandPalette(ClusterCommands.Instance);
                        pbm.RegisterCommandPalette(RemoteCommands.Instance);
                        pbm.Start(); // begin listening for PBM management commands
                    });
            });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment()) app.UseDeveloperExceptionPage();

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                var routerForwarder = endpoints.ServiceProvider.GetRequiredService<ActorRegistry>().Get<RouterForwarderActor>();
                endpoints.MapGet("/", async context =>
                {
                    using (var s = MyTracer.StartActivity("Cluster.Ask", ActivityKind.Client))
                    {
                        // router actor will deliver message randomly to someone in cluster
                        var resp = await routerForwarder.Ask<string>($"hit from {context.TraceIdentifier}",
                            TimeSpan.FromSeconds(5));
                        await context.Response.WriteAsync(resp);
                    }
                });
            });
        }
    }
}
