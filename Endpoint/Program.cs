using Azure.Monitor.OpenTelemetry.Exporter;
using NServiceBus;
using System;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Collections.Generic;
using System.Threading;
using OpenTelemetry.Metrics;

var endpointName = "Samples.OpenTelemetry.AppInsights";

Console.Title = endpointName;

var attributes = new Dictionary<string, object>
{
    ["service.name"] = endpointName,
    ["service.instance.id"] = Guid.NewGuid().ToString(),
};

var appInsightsConnectionString = "<insert-connection-string-here>";
var resourceBuilder = ResourceBuilder.CreateDefault().AddAttributes(attributes);

#region enable-tracing

var traceProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(resourceBuilder)
    .AddSource("NServiceBus.Core")
    .AddAzureMonitorTraceExporter(o => o.ConnectionString = appInsightsConnectionString)
    .AddConsoleExporter()
    .Build();

#endregion

#region enable-meters

var meterProvider = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(resourceBuilder)
    .AddMeter("NServiceBus.Core")
    .AddAzureMonitorMetricExporter(o => o.ConnectionString = appInsightsConnectionString)
    .AddConsoleExporter()
    .Build();

#endregion

#region enable-open-telemetry
var endpointConfiguration = new EndpointConfiguration(endpointName);
endpointConfiguration.EnableOpenTelemetry();
#endregion

#region recoverability
var auditQueue = "audit";
var errorQueue = "error";
endpointConfiguration.AuditProcessedMessagesTo(auditQueue);
endpointConfiguration.SendFailedMessagesTo(errorQueue);

var recoverability = endpointConfiguration.Recoverability();

var immediateRetries = 1;
recoverability.Immediate(immediate => { immediate.NumberOfRetries(immediateRetries); });

recoverability.Delayed(settings =>
        settings
            .NumberOfRetries(2)
            .TimeIncrease(TimeSpan.FromSeconds(10)));

recoverability.CustomPolicy((config, context) =>
{
    if (context.DelayedDeliveriesPerformed >= 2)
    {
        return DefaultRecoverabilityPolicy.Invoke(config, context);
    }

    var delay = 2;

    return RecoverabilityAction.DelayedRetry(TimeSpan.FromSeconds(delay));
});

var numberOfConsecutiveFailures = 3;
var timeToWaitBetweenThrottledAttempts = TimeSpan.FromSeconds(5);

recoverability.OnConsecutiveFailures(
    numberOfConsecutiveFailures,
    new RateLimitSettings(timeToWaitBetweenThrottledAttempts));

#endregion

endpointConfiguration.UseSerialization<SystemJsonSerializer>();
endpointConfiguration.UseTransport<LearningTransport>();
var cancellation = new CancellationTokenSource();
var endpointInstance = await Endpoint.Start(endpointConfiguration, cancellation.Token);

var simulator = new LoadSimulator(endpointInstance, TimeSpan.Zero, TimeSpan.FromSeconds(10));
simulator.Start(cancellation.Token);

try
{
    Console.WriteLine("Endpoint started. Press any key to send a message. Press ESC to stop");

    while (Console.ReadKey(true).Key != ConsoleKey.Escape)
    {
        await endpointInstance.SendLocal(new SomeMessage(), cancellation.Token);
    }
}
finally
{
    await simulator.Stop(cancellation.Token);
    await endpointInstance.Stop(cancellation.Token);
    traceProvider?.Dispose();
    meterProvider?.Dispose();
}
