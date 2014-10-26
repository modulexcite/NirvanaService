﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using EnumLogger.Extensions;
using log4net.Config;
using Newtonsoft.Json;
using NirvanaService.Configuration;

namespace NirvanaService
{
    internal class ServiceWrapper
    {
        private const string ConfigFolderPath = "conf";

        private readonly string _serviceName;

        public ServiceWrapper(string name)
        {
            _serviceName = name;
            InitLogging();
        }

        private void InitLogging()
        {
            var log4NetConfigPath = GetLog4NetPath();
            XmlConfigurator.ConfigureAndWatch(new FileInfo(log4NetConfigPath));
        }

        private string GetLog4NetPath()
        {
            var assemblyInfo = new FileInfo(typeof(ServiceWrapper).Assembly.Location);
            var assemblyFolder = assemblyInfo.Directory;
            var path = Path.Combine(assemblyFolder.FullName, "log4net.config");
            return path;
        }
        
        public void Start()
        {
            var serviceName = GetServiceName();
            var config = GetConfig(serviceName);
            StartProcess(serviceName, config);
        }

        private void StartProcess(string serviceName, ServiceConfig config)
        {
            try
            {
                var startInfo = new ProcessStartInfo(config.Executable.ResolveEnvVariables(),
                    config.Options.ToString().ResolveEnvVariables());
                if (!string.IsNullOrWhiteSpace(config.WorkingDirectory))
                {
                    startInfo.WorkingDirectory = config.WorkingDirectory.ResolveEnvVariables();
                }
                startInfo.UseShellExecute = false;
                var process = Process.Start(startInfo);
                process.Exited += (o, e) => Stop();
                LogEvent.ServiceStarted.Log(GetType(), "Successfully started: {0} with executable: {1}, and arguments: {2}", serviceName, config.Executable.ResolveEnvVariables(), config.Options.ToString().ResolveEnvVariables());
            }
            catch (Exception ex)
            {
                LogEvent.FailedToStartService.Log(GetType(),
                    "Failed to start the service {0} with executable: {1} and arguments: {2}", serviceName, config.Executable.ResolveEnvVariables(),
                    config.Options.ToString(), ex);
                throw ex;
            }
        }

        private string GetServiceName()
        {
            if (!string.IsNullOrWhiteSpace(_serviceName)) return _serviceName;
            try
            {
                var processId = Process.GetCurrentProcess().Id;
                var query = "SELECT * FROM Win32_Service where ProcessId  = " + processId;
                var searcher = new ManagementObjectSearcher(query);
                string serviceName = null;
                foreach (var item in searcher.Get())
                {
                    serviceName = item["Name"].ToString();
                }
                return serviceName;
            }
            catch (Exception)
            {
                LogEvent.FailedToGetServiceName.Log(GetType(), "Failed to lookup the installed service name");
                throw;
            }
        }

        private ServiceConfig GetConfig(string serviceName)
        {
            var currentFolder = Environment.CurrentDirectory;
            var path = Path.Combine(currentFolder, ConfigFolderPath, string.Format("{0}.json", serviceName));
            if (!File.Exists(path))
            {
                LogEvent.MissingConfig.Log(GetType(), "Missing configuration for {0}", serviceName);
                throw new ApplicationException("Missing configuration for " + serviceName);
            }
            try
            {
                return JsonConvert.DeserializeObject<ServiceConfig>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                LogEvent.ConfigFormat.Log(GetType(), "Failed to parse config file: {0} for service: {1}", path, serviceName, ex);
                throw;
            }
        }

        public void Stop()
        {
            try
            {
                var serviceName = GetServiceName();
                LogEvent.StoppingService.Log(GetType(), "Stopping service: {0}", serviceName);

                var processId = Convert.ToUInt32(Process.GetCurrentProcess().Id);
                KillAllProcessesSpawnedBy(processId, processId);

                LogEvent.ServiceStopped.Log(GetType(), "Service stopped: {0}", GetServiceName());
            }
            catch (Exception ex)
            {
                LogEvent.StopFailed.Log(GetType(), "Failed to stop service", ex);
                throw;
            }
        }


        private static void KillAllProcessesSpawnedBy(uint parentProcessId, uint mainProcessId)
        {
            var searcher = new ManagementObjectSearcher(
                "SELECT * " +
                "FROM Win32_Process " +
                "WHERE ParentProcessId=" + parentProcessId);
            var collection = searcher.Get();
            if (collection.Count > 0)
            {
                foreach (var item in collection)
                {
                    var childProcessId = (uint)item["ProcessId"];
                    if (childProcessId != mainProcessId)
                    {
                        if (childProcessId != Process.GetCurrentProcess().Id)
                        {
                            KillAllProcessesSpawnedBy(childProcessId, mainProcessId);

                            KillProcessById(childProcessId);
                        }
                    }
                }
            }
        }

        private static void KillProcessById(uint processId)
        {

            var process = Process.GetProcessById(Convert.ToInt32(processId));
            process.Refresh();

            if (process.HasExited) return;

            process.Kill();
            process.WaitForExit();
            process.Dispose();
        }
    }
}