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
    public class RequestBuilderFactory : IRequestBuilderFactory
    {
        public IRequestBuilder Create(Type interfaceType)
        {
            return new RequestBuilderImplementation(interfaceType);
        }
    }

    public class RequestBuilderImplementation : IRequestBuilder
    {
        readonly Type targetType;
        readonly Dictionary<string, RestMethodInfo> interfaceHttpMethods;

        public RequestBuilderImplementation(Type targetInterface)
        {
            if (targetInterface == null || !targetInterface.IsInterface) {
                throw new ArgumentException("targetInterface must be an Interface");
            }

            targetType = targetInterface;
            interfaceHttpMethods = targetInterface.GetMethods()
                .SelectMany(x => {
                    var attrs = x.GetCustomAttributes(true);
                    var hasHttpMethod = attrs.OfType<HttpMethodAttribute>().Any();
                    if (!hasHttpMethod) return Enumerable.Empty<RestMethodInfo>();

                    return EnumerableEx.Return(new RestMethodInfo(targetInterface, x));
                })
                .ToDictionary(k => k.Name, v => v);
        }

        public IEnumerable<string> InterfaceHttpMethods {
            get { return interfaceHttpMethods.Keys; }
        }

        public Func<object[], HttpRequestMessage> BuildRequestFactoryForMethod(string methodName)
        {
            if (!interfaceHttpMethods.ContainsKey(methodName)) {
                throw new ArgumentException("Method must be defined and have an HTTP Method attribute");
            }
            var restMethod = interfaceHttpMethods[methodName];
            var jsonConverter = GetJsonSerializer<string>();
            return paramList => {
                var ret = new HttpRequestMessage() {
                    Method = restMethod.HttpMethod,
                };

                var urlTarget = new StringBuilder(restMethod.RelativePath);
                var queryParamsToAdd = new Dictionary<string, string>();

                for(int i=0; i < paramList.Length; i++) {
                    if (restMethod.ParameterMap.ContainsKey(i)) {
                        urlTarget.Replace("{" + restMethod.ParameterMap[i] + "}", paramList[i].ToString());
                        continue;
                    }

                    if (restMethod.BodyParameterInfo != null && restMethod.BodyParameterInfo.Item2 == i) {
                        var streamParam = paramList[i] as Stream;
                        var stringParam = paramList[i] as string;

                        if (streamParam != null) {
                            ret.Content = new StreamContent(streamParam);
                        } else if (stringParam != null) {
                            ret.Content = new StringContent(stringParam);
                        } else {
                            ret.Content = new StringContent(jsonConverter.serialize(paramList[i]), Encoding.UTF8, "application/json");
                        }

                        continue;
                    }

                    if (paramList[i] != null) {
                        queryParamsToAdd[restMethod.QueryParameterMap[i]] = paramList[i].ToString();
                    }
                }

                // NB: The URI methods in .NET are dumb. Also, we do this 
                // UriBuilder business so that we preserve any hardcoded query 
                // parameters as well as add the parameterized ones.
                var uri = new UriBuilder(new Uri(new Uri("http://api"), urlTarget.ToString()));
                var query = HttpUtility.ParseQueryString(uri.Query ?? "");
                foreach(var kvp in queryParamsToAdd) {
                    query.Add(kvp.Key, kvp.Value);
                }

                if (query.HasKeys()) {
                    var pairs = query.Keys.Cast<string>().Select(x => HttpUtility.UrlEncode(x) + "=" + HttpUtility.UrlEncode(query[x]));
                    uri.Query = String.Join("&", pairs);
                } else {
                    uri.Query = null;
                }

                ret.RequestUri = new Uri(uri.Uri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped), UriKind.Relative);
                return ret;
            };
        }

        public Func<HttpClient, object[], object> BuildRestResultFuncForMethod(string methodName)
        {
            if (!interfaceHttpMethods.ContainsKey(methodName)) {
                throw new ArgumentException("Method must be defined and have an HTTP Method attribute");
            }

            var restMethod = interfaceHttpMethods[methodName];

            if (restMethod.ReturnType == typeof(Task)) {
                return buildVoidTaskFuncForMethod(restMethod);
            } else if (restMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)) {
                // NB: This jacked up reflection code is here because it's
                // difficult to upcast Task<object> to an arbitrary T, especially
                // if you need to AOT everything, so we need to reflectively 
                // invoke buildTaskFuncForMethod.
                var taskFuncMi = GetType().GetMethod("buildTaskFuncForMethod", BindingFlags.NonPublic | BindingFlags.Instance);
                var taskFunc = (MulticastDelegate)taskFuncMi.MakeGenericMethod(restMethod.SerializedReturnType)
                    .Invoke(this, new[] { restMethod });

                return (client, args) => {
                    return taskFunc.DynamicInvoke(new object[] { client, args } );
                };
            } else {
                // Same deal
                var rxFuncMi = GetType().GetMethod("buildRxFuncForMethod", BindingFlags.NonPublic | BindingFlags.Instance);
                var rxFunc = (MulticastDelegate)rxFuncMi.MakeGenericMethod(restMethod.SerializedReturnType)
                    .Invoke(this, new[] { restMethod });

                return (client, args) => {
                    return rxFunc.DynamicInvoke(new object[] { client, args });
                };
            }
        }

        Func<HttpClient, object[], Task> buildVoidTaskFuncForMethod(RestMethodInfo restMethod)
        {
            var factory = BuildRequestFactoryForMethod(restMethod.Name);
                        
            return async (client, paramList) => {
                var rq = factory(paramList);
                var resp = await client.SendAsync(rq);

                resp.EnsureSuccessStatusCode();
            };
        }

        public IJsonConverter<T> GetJsonSerializer<T>()
        {
            return new JsonDotNetConverter<T>();
        }

        Func<HttpClient, object[], Task<T>> buildTaskFuncForMethod<T>(RestMethodInfo restMethod)
            where T : class
        {
            var factory = BuildRequestFactoryForMethod(restMethod.Name);
            var jsonSerializer = GetJsonSerializer<T>();

            return async (client, paramList) => {
                var rq = factory(paramList);
                var resp = await client.SendAsync(rq);
                if (restMethod.SerializedReturnType == typeof(HttpResponseMessage)) {
                    return resp as T;
                }

                resp.EnsureSuccessStatusCode();

                var content = await resp.Content.ReadAsStringAsync();
                if (restMethod.SerializedReturnType == typeof(string)) {
                    return content as T;
                }

                return jsonSerializer.Deserialize(content);
            };
        }

        Func<HttpClient, object[], IObservable<T>> buildRxFuncForMethod<T>(RestMethodInfo restMethod)
            where T : class
        {
            var taskFunc = buildTaskFuncForMethod<T>(restMethod);

            return (client, paramList) => {
                var ret = new FakeAsyncSubject<T>();

                taskFunc(client, paramList).ContinueWith(t => {
                    if (t.Exception != null) {
                        ret.OnError(t.Exception);
                    } else {
                        ret.OnNext(t.Result);
                        ret.OnCompleted();
                    }
                });

                return ret;
            };
        }

        class CompletionResult 
        {
            public bool IsCompleted { get; set; }
            public Exception Error { get; set; }
        }

        class FakeAsyncSubject<T> : IObservable<T>, IObserver<T>
        {
            bool resultSet;
            T result;
            CompletionResult completion;
            List<IObserver<T>> subscriberList = new List<IObserver<T>>();

            public void OnNext(T value)
            {
                if (completion == null) return;

                result = value;
                resultSet = true;

                var currentList = default(IObserver<T>[]);
                lock (subscriberList) { currentList = subscriberList.ToArray(); }
                foreach (var v in currentList) v.OnNext(value);
            }

            public void OnError(Exception error)
            {
                var final = Interlocked.CompareExchange(ref completion, new CompletionResult() { IsCompleted = false, Error = error }, null);
                if (final.IsCompleted) return;
                                
                var currentList = default(IObserver<T>[]);
                lock (subscriberList) { currentList = subscriberList.ToArray(); }
                foreach (var v in currentList) v.OnError(error);

                final.IsCompleted = true;
            }

            public void OnCompleted()
            {
                var final = Interlocked.CompareExchange(ref completion, new CompletionResult() { IsCompleted = false, Error = null }, null);
                if (final.IsCompleted) return;
                                
                var currentList = default(IObserver<T>[]);
                lock (subscriberList) { currentList = subscriberList.ToArray(); }
                foreach (var v in currentList) v.OnCompleted();

                final.IsCompleted = true;
            }

            public IDisposable Subscribe(IObserver<T> observer)
            {
                if (completion != null) {
                    if (completion.Error != null) {
                        observer.OnError(completion.Error);
                        return new AnonymousDisposable(() => {});
                    }

                    if (resultSet) observer.OnNext(result);
                    observer.OnCompleted();
                        
                    return new AnonymousDisposable(() => {});
                }

                lock (subscriberList) { 
                    subscriberList.Add(observer);
                }

                return new AnonymousDisposable(() => {
                    lock (subscriberList) { subscriberList.Remove(observer); }
                });
            }
        }
    }

    sealed class AnonymousDisposable : IDisposable
    {
        readonly Action block;

        public AnonymousDisposable(Action block)
        {
            this.block = block;
        }

        public void Dispose()
        {
            block();
        }
    }

    public class RestMethodInfo
    {
        public string Name { get; set; }
        public Type Type { get; set; }
        public MethodInfo MethodInfo { get; set; }
        public HttpMethod HttpMethod { get; set; }
        public string RelativePath { get; set; }
        public Dictionary<int, string> ParameterMap { get; set; }
        public Tuple<BodySerializationMethod, int> BodyParameterInfo { get; set; }
        public Dictionary<int, string> QueryParameterMap { get; set; }
        public Type ReturnType { get; set; }
        public Type SerializedReturnType { get; set; }

        static readonly Regex parameterRegex = new Regex(@"^{(.*)}$");

        public RestMethodInfo(Type targetInterface, MethodInfo methodInfo)
        {
            Type = targetInterface;
            Name = methodInfo.Name;
            MethodInfo = methodInfo;

            var hma = methodInfo.GetCustomAttributes(true)
                .OfType<HttpMethodAttribute>()
                .First();

            HttpMethod = hma.Method;
            RelativePath = hma.Path;

            verifyUrlPathIsSane(RelativePath);
            determineReturnTypeInfo(methodInfo);

            var parameterList = methodInfo.GetParameters().ToList();

            ParameterMap = buildParameterMap(RelativePath, parameterList);
            BodyParameterInfo = findBodyParameter(parameterList);

            QueryParameterMap = new Dictionary<int, string>();
            for (int i=0; i < parameterList.Count; i++) {
                if (ParameterMap.ContainsKey(i) || (BodyParameterInfo != null && BodyParameterInfo.Item2 == i)) {
                    continue;
                }

                QueryParameterMap[i] = getUrlNameForParameter(parameterList[i]);
            }
        }

        void verifyUrlPathIsSane(string relativePath)
        {
            if (!relativePath.StartsWith("/")) {
                goto bogusPath;
            }

            var parts = relativePath.Split('/');
            if (parts.Length == 0) {
                goto bogusPath;
            }

            return;

        bogusPath:
            throw new ArgumentException("URL path must be of the form '/foo/bar/baz'");
        }

        Dictionary<int, string> buildParameterMap(string relativePath, List<ParameterInfo> parameterInfo)
        {
            var ret = new Dictionary<int, string>();

            var parameterizedParts = relativePath.Split('/', '?').SelectMany(x => {
                var m = parameterRegex.Match(x);
                return (m.Success ? EnumerableEx.Return(m) : Enumerable.Empty<Match>());
            }).ToList();

            if (parameterizedParts.Count == 0) {
                return ret;
            }

            var paramValidationDict = parameterInfo.ToDictionary(k => getUrlNameForParameter(k).ToLowerInvariant(), v => v);

            foreach (var match in parameterizedParts) {
                var name = match.Groups[1].Value.ToLowerInvariant();
                if (!paramValidationDict.ContainsKey(name)) {
                    throw new ArgumentException(String.Format("URL has parameter {0}, but no method parameter matches", name));
                }

                ret.Add(parameterInfo.IndexOf(paramValidationDict[name]), name);
            }

            return ret;
        }

        string getUrlNameForParameter(ParameterInfo paramInfo)
        {
            var aliasAttr = paramInfo.GetCustomAttributes(true)
                .OfType<AliasAsAttribute>()
                .FirstOrDefault();
            return aliasAttr != null ? aliasAttr.Name : paramInfo.Name;
        }

        Tuple<BodySerializationMethod, int> findBodyParameter(List<ParameterInfo> parameterList)
        {
            var bodyParams = parameterList
                .Select(x => new { Parameter = x, BodyAttribute = x.GetCustomAttributes(true).OfType<BodyAttribute>().FirstOrDefault() })
                .Where(x => x.BodyAttribute != null)
                .ToList();

            if (bodyParams.Count > 1) {
                throw new ArgumentException("Only one parameter can be a Body parameter");
            }

            if (bodyParams.Count == 0) {
                return null;
            }

            var ret = bodyParams[0];
            return Tuple.Create(ret.BodyAttribute.SerializationMethod, parameterList.IndexOf(ret.Parameter));
        }

        void determineReturnTypeInfo(MethodInfo methodInfo)
        {
            if (methodInfo.ReturnType.IsGenericType == false) {
                if (methodInfo.ReturnType != typeof (Task)) {
                    goto bogusMethod;
                }

                ReturnType = methodInfo.ReturnType;
                SerializedReturnType = typeof(void);
                return;
            }

            var genericType = methodInfo.ReturnType.GetGenericTypeDefinition();
            if (genericType != typeof(Task<>) && genericType != typeof(IObservable<>)) {
                goto bogusMethod;
            }

            ReturnType = methodInfo.ReturnType;
            SerializedReturnType = methodInfo.ReturnType.GetGenericArguments()[0];
            return;

        bogusMethod:
            throw new ArgumentException("All REST Methods must return either Task<T> or IObservable<T>");
        }
    }
}
