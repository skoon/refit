using System;
using System.Net.Http;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Text;
using Newtonsoft.Json;
using System.IO;
using System.Web;
using System.Threading;

namespace Refit
{
    public static class RefitSettings
    {
        public static Func<JsonSettings> GetJsonSettings { get; set; }
    }
}
