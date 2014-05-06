using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Refit;
using ServiceStack.Text;

namespace ServiceStackJsonForRefit
{
    public class ServiceStackConverter<T> : IJsonConverter<T>
    {
        public string serialize(object o)
        {
            return JsonSerializer.SerializeToString(o);
        }

        public T Deserialize(string jsonString)
        {
            return JsonSerializer.DeserializeFromString<T>(jsonString);
        }
    }
}
