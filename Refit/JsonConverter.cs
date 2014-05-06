using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Refit
{

    public interface ISerializeStuff
    {
        string serialize(object o);
    }

    public interface IDeserializeStuff<T>
    {
        T Deserialize(string jsonString);
    }

    public interface IJsonConverter<T> : ISerializeStuff, IDeserializeStuff<T> { }

    public class JsonDotNetConverter<T> : IJsonConverter<T>
    {

        public string serialize(object o)
        {
            return JsonConvert.SerializeObject(o);
        }

        public T Deserialize(string jsonString)
        {
            return JsonConvert.DeserializeObject<T>(jsonString);
        }
    }
}

