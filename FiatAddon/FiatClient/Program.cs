﻿using System.Collections.Concurrent;
using System.Globalization;
using Cocona;
using CoordinateSharp;
using FiatUconnect;
using FiatUconnect.HA;
using Flurl.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;

var builder = CoconaApp.CreateBuilder();

builder.Configuration.AddEnvironmentVariables("FiatUconnect_");

//todo: integrate reports and events
//todo: schedule turn charging off
//todo: better handling of auto refresh battery and location ...

builder.Services.AddOptions<AppConfig>()
  .Bind(builder.Configuration)
  .ValidateDataAnnotations()
  .ValidateOnStart();

var app = builder.Build();

var persistentHaEntities = new ConcurrentDictionary<string, IEnumerable<HaEntity>>();
var appConfig = builder.Configuration.Get<AppConfig>();
var forceLoopResetEvent = new AutoResetEvent(false);
var haClient = new HaRestApi(appConfig.HomeAssistantUrl, appConfig.SupervisorToken);

Log.Logger = new LoggerConfiguration()
  .MinimumLevel.Is(appConfig.Debug ? LogEventLevel.Debug : LogEventLevel.Information)
  .WriteTo.Console()
  .CreateLogger();

Log.Information("Delay start for seconds: {0}", appConfig.StartDelaySeconds);
await Task.Delay(TimeSpan.FromSeconds(appConfig.StartDelaySeconds));


await app.RunAsync(async (CoconaAppContext ctx) =>
{
  Log.Information("{0}", appConfig.ToStringWithoutSecrets());
  Log.Debug("{0}", appConfig.Dump());

  IFiatClient fiatClient = new FiatClient(appConfig.FiatUser, appConfig.FiatPw);

  var mqttClient = new SimpleMqttClient(appConfig.MqttServer,
    appConfig.MqttPort,
    appConfig.MqttUser,
    appConfig.MqttPw,
    "FiatUconnect");

  await mqttClient.Connect();

  while (!ctx.CancellationToken.IsCancellationRequested)
  {
    Log.Information("Now fetching new data...");

    GC.Collect();

    try
    {
      await fiatClient.LoginAndKeepSessionAlive();

      foreach (var vehicle in await fiatClient.Fetch())
      {
        Log.Information("FOUND CAR: {0}", vehicle.Vin);

        await Task.Delay(TimeSpan.FromSeconds(10), ctx.CancellationToken);

        var vehicleName = string.IsNullOrEmpty(vehicle.Nickname) ? "Fiat" : vehicle.Nickname;

        var haDevice = new HaDevice()
        {
          Name = vehicleName,
          Identifier = vehicle.Vin,
          Manufacturer = vehicle.Make,
          Model = vehicle.ModelDescription,
          Version = "1.0"
        };

        var currentCarLocation = new Coordinate(vehicle.Location.Latitude, vehicle.Location.Longitude);

        var zones = await haClient.GetZonesAscending(currentCarLocation);

        Log.Debug("Zones: {0}", zones.Dump());

        var tracker = new HaDeviceTracker(mqttClient, "500e_Location", haDevice)
        {
          Lat = currentCarLocation.Latitude.ToDouble(),
          Lon = currentCarLocation.Longitude.ToDouble(),
          StateValue = zones.FirstOrDefault()?.FriendlyName ?? "Away"
        };

        Log.Information("Car is at location: {0}", tracker.Dump());

        Log.Debug("Announce sensor: {0}", tracker.Dump());
        await tracker.Announce();
        await tracker.PublishState();

        var compactDetails = vehicle.Details.Compact("500e");
        var unitSystem = await haClient.GetUnitSystem();

        Log.Information("Using unit system: {0}", unitSystem.Dump());

        var sensors = compactDetails.Select(detail =>
        {

          var binary = detail.Key.Contains("scheduleddays");

          var sensor = new HaSensor(mqttClient, detail.Key, haDevice,true)
          {
            Value = detail.Value
          };

          if (detail.Key.EndsWith("_value"))
          {
            var unitKey = detail.Key.Replace("_value", "_unit");

            compactDetails.TryGetValue(unitKey, out var tmpUnit);

            switch (tmpUnit)
            {
              case "km":
                sensor.DeviceClass = "distance";
                sensor.Unit = "km";
                break;
              case "volts":
                sensor.DeviceClass = "voltage";
                sensor.Unit = "V";
                break;
              case null or "null":
                sensor.Unit = "";
                break;
              default:
                sensor.Unit = tmpUnit;
                break;
            }
          }

          if (detail.Key.EndsWith("evInfo_battery_stateOfCharge"))
          {
              sensor.DeviceClass = "battery";
              sensor.Unit = "%";
          }

          if (detail.Key.Contains("evInfo_battery_timeToFullyChargeL"))
          {
              sensor.DeviceClass = "duration";
              sensor.Unit = "min";
          }

          return sensor;
        }).ToDictionary(k => k.Name, v => v);
       

        Log.Debug("Announce sensors: {0}", sensors.Dump());
        Log.Information("Pushing new sensors and values to Home Assistant");

        await Parallel.ForEachAsync(sensors.Values, async (sensor, token) => { await sensor.Announce(); });

        Log.Debug("Waiting for home assistant to process all sensors");
        await Task.Delay(TimeSpan.FromSeconds(5), ctx.CancellationToken);

        await Parallel.ForEachAsync(sensors.Values, async (sensor, token) => { await sensor.PublishState(); });

        var lastUpdate = new HaSensor(mqttClient, "500e_LastUpdate", haDevice,false)
        {
          Value = DateTime.Now.ToString("O"),
          DeviceClass = "timestamp"
        };

        await lastUpdate.Announce();
        await lastUpdate.PublishState();

        var haEntities = persistentHaEntities.GetOrAdd(vehicle.Vin, s =>
          CreateInteractiveEntities(ctx,fiatClient, mqttClient, vehicle, haDevice));

        foreach (var haEntity in haEntities)
        {
          Log.Debug("Announce sensor: {0}", haEntity.Dump());
          await haEntity.Announce();
        }
      }
    }
    catch (FlurlHttpException httpException)
    {
      Log.Warning($"Error connecting to the FIAT API. \n" +
                  $"This can happen from time to time. Retrying in {appConfig.RefreshInterval} minutes.");

      Log.Debug("ERROR: {0}", httpException.Message);
      Log.Debug("STATUS: {0}", httpException.StatusCode);

      var task = httpException.Call?.Response?.GetStringAsync();

      if (task != null)
      {
        Log.Debug("RESPONSE: {0}", await task);
      }
    }
    catch (Exception e)
    {
      Log.Error("{0}", e);
    }

    Log.Information("Fetching COMPLETED. Next update in {0} minutes.", appConfig.RefreshInterval);

    WaitHandle.WaitAny(new[]
    {
      ctx.CancellationToken.WaitHandle,
      forceLoopResetEvent
    }, TimeSpan.FromMinutes(appConfig.RefreshInterval));
  }
});

async Task<bool> TrySendCommand(IFiatClient fiatClient, FiatCommand command, string vin)
{
  Log.Information("SEND COMMAND {0}: ", command.Message);

  if (string.IsNullOrWhiteSpace(appConfig.FiatPin))
  {
    throw new Exception("PIN NOT SET");
  }

  var pin = appConfig.FiatPin;

  try
  {
    await fiatClient.SendCommand(vin, command.Message, pin, command.Action);
    await Task.Delay(TimeSpan.FromSeconds(5));
    Log.Information("Command: {0} SUCCESSFUL", command.Message);
  }
  catch (Exception e)
  {
    Log.Error("Command: {0} ERROR. Maybe wrong pin?", command.Message);
    Log.Debug("{0}", e);
    return false;
  }

  return true;
}

IEnumerable<HaEntity> CreateInteractiveEntities(CoconaAppContext ctx,IFiatClient fiatClient, SimpleMqttClient mqttClient, Vehicle vehicle,
  HaDevice haDevice)
{
  var updateLocationButton = new HaButton(mqttClient, "UpdateLocation", haDevice, async button =>
  {
    if (await TrySendCommand(fiatClient, FiatCommand.VF, vehicle.Vin))
    {
      await Task.Delay(TimeSpan.FromSeconds(3), ctx.CancellationToken);
      forceLoopResetEvent.Set();
    }
  });

  var deepRefreshButton = new HaButton(mqttClient, "DeepRefresh", haDevice, async button =>
  {
    if (await TrySendCommand(fiatClient, FiatCommand.DEEPREFRESH, vehicle.Vin))
    {
      await Task.Delay(TimeSpan.FromSeconds(3), ctx.CancellationToken);
      forceLoopResetEvent.Set();
    }
  });

  var lightsButton = new HaButton(mqttClient, "Light", haDevice, async button =>
  {
    if (await TrySendCommand(fiatClient, FiatCommand.ROLIGHTS, vehicle.Vin))
    {
      forceLoopResetEvent.Set();
    }
  });

  var chargeNowButton = new HaButton(mqttClient, "ChargeNOW", haDevice, async button =>
  {
    if (await TrySendCommand(fiatClient, FiatCommand.CNOW, vehicle.Vin))
    {
      forceLoopResetEvent.Set();
    }
  });



  var hvacButton = new HaButton(mqttClient, "HVAC", haDevice, async button =>
  {
    if (await TrySendCommand(fiatClient, FiatCommand.ROPRECOND, vehicle.Vin))
    {
      forceLoopResetEvent.Set();
    }
  });
  
  
  var lockButton = new HaButton(mqttClient, "DoorLock", haDevice, async button =>
  {
    if (await TrySendCommand(fiatClient, FiatCommand.RDL, vehicle.Vin))
    {
      forceLoopResetEvent.Set();
    }
  });
  
  var unLockButton = new HaButton(mqttClient, "DoorUnLock", haDevice, async button =>
  {
    if (await TrySendCommand(fiatClient, FiatCommand.RDU, vehicle.Vin))
    {
      forceLoopResetEvent.Set();
    }
  });


  return new HaEntity[]
  {
    hvacButton,
    chargeNowButton,
    deepRefreshButton,
    lightsButton,
    updateLocationButton,
    lockButton,
    unLockButton,
  };
}
