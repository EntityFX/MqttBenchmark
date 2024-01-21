using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityFX.MqttBenchmark.Bomber.Settings
{
    internal class ScenarioTemplate : ICloneable
    {
        internal class CustomSettingsTemplate
        {
            public string Topic { get; set; }

            public int Qos { get; set; }

            public int ClientsCount { get; set; }

            public string Server { get; set; }

            public int Port { get; set; }

            public int MessageSize { get; set; }
        }

        internal class LoadSimulationsSettingsTemplate
        {
            public object[] KeepConstant { get; set; }
        }

        public string ScenarioName { get; set; }

        public CustomSettingsTemplate CustomSettings { get; set; }

        public LoadSimulationsSettingsTemplate[] LoadSimulationsSettings { get; set; }

        public int MaxFailCount { get; set; }

        public object Clone()
        {
            return this.MemberwiseClone(); 
        }
    }
}
