namespace Web10.Radio.API

open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open OpenTelemetry.Metrics
open OpenTelemetry.Resources
open OpenTelemetry.Trace

module ObservabilityComposition =
    let addObservability (otel: OtelOptions) (_environment: IHostEnvironment) (services: IServiceCollection) : IServiceCollection =
        services
            .AddOpenTelemetry()
            .ConfigureResource(fun resource -> resource.AddService(serviceName = "Web10.Radio.API") |> ignore)
            .WithTracing(fun tracing ->
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddOtlpExporter(fun exporter -> exporter.Endpoint <- otel.ExporterOtlpEndpoint)
                |> ignore)
            .WithMetrics(fun metrics ->
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddOtlpExporter(fun exporter -> exporter.Endpoint <- otel.ExporterOtlpEndpoint)
                |> ignore)
        |> ignore

        services
