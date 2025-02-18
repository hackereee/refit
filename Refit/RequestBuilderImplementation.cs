﻿using System;
using System.Collections;
using System.Reflection;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;
using System.Collections.Concurrent;
using System.Net.Http.Headers;

namespace Refit
{
    class RequestBuilderImplementation<TApi> : RequestBuilderImplementation, IRequestBuilder<TApi>
    {
        public RequestBuilderImplementation(RefitSettings refitSettings = null) : base(typeof(TApi), refitSettings)
        {
        }
    }

    partial class RequestBuilderImplementation : IRequestBuilder
    {
        static readonly ISet<HttpMethod> BodylessMethods = new HashSet<HttpMethod>
        {
            HttpMethod.Get,
            HttpMethod.Head
        };
        readonly Dictionary<string, List<RestMethodInfo>> interfaceHttpMethods;
        readonly ConcurrentDictionary<CloseGenericMethodKey, RestMethodInfo> interfaceGenericHttpMethods;
        readonly IContentSerializer serializer;
        readonly RefitSettings settings;
        public Type TargetType { get; }

        public RequestBuilderImplementation(Type refitInterfaceType, RefitSettings refitSettings = null)
        {
            var targetInterfaceInheritedInterfaces = refitInterfaceType.GetInterfaces();

            settings = refitSettings ?? new RefitSettings();
            serializer = settings.ContentSerializer;
            interfaceGenericHttpMethods = new ConcurrentDictionary<CloseGenericMethodKey, RestMethodInfo>();

            if (refitInterfaceType == null || !refitInterfaceType.GetTypeInfo().IsInterface)
            {
                throw new ArgumentException("targetInterface must be an Interface");
            }

            TargetType = refitInterfaceType;

            var dict = new Dictionary<string, List<RestMethodInfo>>();

            AddInterfaceHttpMethods(refitInterfaceType, dict);
            foreach (var inheritedInterface in targetInterfaceInheritedInterfaces)
            {
                AddInterfaceHttpMethods(inheritedInterface, dict);
            }

            interfaceHttpMethods = dict;
        }

        void AddInterfaceHttpMethods(Type interfaceType, Dictionary<string, List<RestMethodInfo>> methods)
        {
            foreach (var methodInfo in interfaceType.GetMethods())
            {
                var attrs = methodInfo.GetCustomAttributes(true);
                var hasHttpMethod = attrs.OfType<HttpMethodAttribute>().Any();
                if (hasHttpMethod)
                {
                    if (!methods.ContainsKey(methodInfo.Name))
                    {
                        methods.Add(methodInfo.Name, new List<RestMethodInfo>());
                    }

                    var restinfo = new RestMethodInfo(interfaceType, methodInfo, settings);
                    methods[methodInfo.Name].Add(restinfo);
                }
            }
        }

        RestMethodInfo FindMatchingRestMethodInfo(string key, Type[] parameterTypes, Type[] genericArgumentTypes)
        {
            if (interfaceHttpMethods.TryGetValue(key, out var httpMethods))
            {
                if (parameterTypes == null)
                {
                    if (httpMethods.Count > 1)
                    {
                        throw new ArgumentException($"MethodName exists more than once, '{nameof(parameterTypes)}' mut be defined");
                    }
                    return CloseGenericMethodIfNeeded(httpMethods[0], genericArgumentTypes);
                }

                var isGeneric = genericArgumentTypes?.Length > 0;

                var possibleMethodsList = httpMethods.Where(method => method.MethodInfo.GetParameters().Length == parameterTypes.Count());

                // If it's a generic method, add that filter
                if (isGeneric)
                    possibleMethodsList = possibleMethodsList.Where(method => method.MethodInfo.IsGenericMethod && method.MethodInfo.GetGenericArguments().Length == genericArgumentTypes.Length);
                else // exclude generic methods
                    possibleMethodsList = possibleMethodsList.Where(method => !method.MethodInfo.IsGenericMethod);

                var possibleMethods = possibleMethodsList.ToList();

                if (possibleMethods.Count == 1)
                    return CloseGenericMethodIfNeeded(possibleMethods[0], genericArgumentTypes);

                var parameterTypesArray = parameterTypes.ToArray();
                foreach (var method in possibleMethods)
                {
                    var match = method.MethodInfo.GetParameters()
                                      .Select(p => p.ParameterType)
                                      .SequenceEqual(parameterTypesArray);
                    if (match)
                    {
                        return CloseGenericMethodIfNeeded(method, genericArgumentTypes);
                    }
                }

                throw new Exception("No suitable Method found...");
            }
            else
            {
                throw new ArgumentException("Method must be defined and have an HTTP Method attribute");
            }

        }

        RestMethodInfo CloseGenericMethodIfNeeded(RestMethodInfo restMethodInfo, Type[] genericArgumentTypes)
        {
            if (genericArgumentTypes != null)
            {
                return interfaceGenericHttpMethods.GetOrAdd(new CloseGenericMethodKey(restMethodInfo.MethodInfo, genericArgumentTypes),
                    _ => new RestMethodInfo(restMethodInfo.Type, restMethodInfo.MethodInfo.MakeGenericMethod(genericArgumentTypes), restMethodInfo.RefitSettings));
            }
            return restMethodInfo;
        }

        public Func<HttpClient, object[], object> BuildRestResultFuncForMethod(string methodName, Type[] parameterTypes = null, Type[] genericArgumentTypes = null)
        {
            if (!interfaceHttpMethods.ContainsKey(methodName))
            {
                throw new ArgumentException("Method must be defined and have an HTTP Method attribute");
            }

            var restMethod = FindMatchingRestMethodInfo(methodName, parameterTypes, genericArgumentTypes);
            if (restMethod.ReturnType == typeof(Task))
            {
                return BuildVoidTaskFuncForMethod(restMethod);
            }

            if (restMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                // NB: This jacked up reflection code is here because it's
                // difficult to upcast Task<object> to an arbitrary T, especially
                // if you need to AOT everything, so we need to reflectively 
                // invoke buildTaskFuncForMethod.
                var taskFuncMi = typeof(RequestBuilderImplementation).GetMethod(nameof(BuildTaskFuncForMethod), BindingFlags.NonPublic | BindingFlags.Instance);
                var taskFunc = (MulticastDelegate)(restMethod.IsApiResponse ?
                    taskFuncMi.MakeGenericMethod(restMethod.SerializedReturnType, restMethod.SerializedGenericArgument) :
                    taskFuncMi.MakeGenericMethod(restMethod.SerializedReturnType, restMethod.SerializedReturnType)).Invoke(this, new[] { restMethod });

                return (client, args) => taskFunc.DynamicInvoke(client, args);
            }

            // Same deal
            var rxFuncMi = typeof(RequestBuilderImplementation).GetMethod(nameof(BuildRxFuncForMethod), BindingFlags.NonPublic | BindingFlags.Instance);
            var rxFunc = (MulticastDelegate)(restMethod.IsApiResponse ?
                    rxFuncMi.MakeGenericMethod(restMethod.SerializedReturnType, restMethod.SerializedGenericArgument) :
                    rxFuncMi.MakeGenericMethod(restMethod.SerializedReturnType, restMethod.SerializedReturnType)).Invoke(this, new[] { restMethod });

            return (client, args) => rxFunc.DynamicInvoke(client, args);
        }

        async Task AddMultipartItemAsync(MultipartFormDataContent multiPartContent, string fileName, string parameterName, object itemValue)
        {

            if (itemValue is HttpContent content)
            {
                multiPartContent.Add(content);
                return;
            }
            if (itemValue is MultipartItem multipartItem)
            {
                var httpContent = multipartItem.ToContent();
                multiPartContent.Add(httpContent, parameterName, string.IsNullOrEmpty(multipartItem.FileName) ? fileName : multipartItem.FileName);
                return;
            }

            if (itemValue is Stream streamValue)
            {
                var streamContent = new StreamContent(streamValue);
                multiPartContent.Add(streamContent, parameterName, fileName);
                return;
            }

            if (itemValue is string stringValue)
            {
                multiPartContent.Add(new StringContent(stringValue), parameterName);
                return;
            }

            if (itemValue is FileInfo fileInfoValue)
            {
                var fileContent = new StreamContent(fileInfoValue.OpenRead());
                multiPartContent.Add(fileContent, parameterName, fileInfoValue.Name);
                return;
            }

            if (itemValue is byte[] byteArrayValue)
            {
                var fileContent = new ByteArrayContent(byteArrayValue);
                multiPartContent.Add(fileContent, parameterName, fileName);
                return;
            }

            // Fallback to serializer
            Exception e;
            try
            {
                multiPartContent.Add(await settings.ContentSerializer.SerializeAsync(itemValue).ConfigureAwait(false), parameterName);
                return;
            }
            catch (Exception ex)
            {
                // Eat this since we're about to throw as a fallback anyway
                e = ex;
            }

            throw new ArgumentException($"Unexpected parameter type in a Multipart request. Parameter {fileName} is of type {itemValue.GetType().Name}, whereas allowed types are String, Stream, FileInfo, Byte array and anything that's JSON serializable", nameof(itemValue), e);
        }

        Func<HttpClient, CancellationToken, object[], Task<T>> BuildCancellableTaskFuncForMethod<T, TBody>(RestMethodInfo restMethod)
        {
            return async (client, ct, paramList) =>
            {
                if (client.BaseAddress == null)
                    throw new InvalidOperationException("BaseAddress must be set on the HttpClient instance");

                var factory = BuildRequestFactoryForMethod(restMethod, client.BaseAddress.AbsolutePath, restMethod.CancellationToken != null);
                var rq = await factory(paramList).ConfigureAwait(false);
                HttpResponseMessage resp = null;
                HttpContent content = null;
                var disposeResponse = true;
                try
                {
                    resp = await client.SendAsync(rq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    content = resp.Content ?? new StringContent(string.Empty);

                    if (restMethod.SerializedReturnType == typeof(HttpResponseMessage))
                    {
                        disposeResponse = false; // caller has to dispose

                        // NB: This double-casting manual-boxing hate crime is the only way to make 
                        // this work without a 'class' generic constraint. It could blow up at runtime 
                        // and would be A Bad Idea if we hadn't already vetted the return type.
                        return (T)(object)resp;
                    }

                    if (!resp.IsSuccessStatusCode)
                    {
                        disposeResponse = false;

                        var exception = await ApiException.Create(rq, restMethod.HttpMethod, resp, restMethod.RefitSettings).ConfigureAwait(false);

                        if (restMethod.IsApiResponse)
                        {
                            return ApiResponse.Create<T>(resp, default(T), exception);
                        }

                        throw exception;
                    }

                    var serializedReturnType = restMethod.IsApiResponse ? restMethod.SerializedGenericArgument : restMethod.SerializedReturnType;

                    if (serializedReturnType == typeof(HttpContent))
                    {
                        disposeResponse = false; // caller has to clean up the content
                        if (restMethod.IsApiResponse)
                            return ApiResponse.Create<T>(resp, content);
                        return (T)(object)content;
                    }

                    if (serializedReturnType == typeof(Stream))
                    {
                        disposeResponse = false; // caller has to dispose
                        var stream = (object)await content.ReadAsStreamAsync().ConfigureAwait(false);
                        if (restMethod.IsApiResponse)
                            return ApiResponse.Create<T>(resp, stream);
                        return (T)stream;
                    }

                    if (serializedReturnType == typeof(string))
                    {
                        using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
                        using var reader = new StreamReader(stream);
                        var str = (object)await reader.ReadToEndAsync().ConfigureAwait(false);
                        if (restMethod.IsApiResponse)
                            return ApiResponse.Create<T>(resp, str);
                        return (T)str;
                    }

                    var body = await serializer.DeserializeAsync<TBody>(content);
                    if (restMethod.IsApiResponse)
                    {
                        return ApiResponse.Create<T>(resp, body);
                    }

                    //  Unfortunate side-effect of having no 'class' or 'T : TBody' constraints.
                    //  However, we know that T must be the same as TBody because IsApiResponse != true so 
                    //  this code is safe at runtime.
                    return (T)(object)body;
                }
                finally
                {
                    // Ensure we clean up the request
                    // Especially important if it has open files/streams
                    rq.Dispose();
                    if (disposeResponse)
                    {
                        resp?.Dispose();
                        content?.Dispose();
                    }
                }
            };
        }

        List<KeyValuePair<string, object>> BuildQueryMap(object @object, string delimiter = null)
        {
            if (@object is IDictionary idictionary)
            {
                return BuildQueryMap(idictionary, delimiter);
            }

            var kvps = new List<KeyValuePair<string, object>>();

            var props = @object.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.CanRead && p.GetMethod.IsPublic);

            foreach (var propertyInfo in props)
            {
                var obj = propertyInfo.GetValue(@object);
                if (obj == null)
                    continue;

                var key = propertyInfo.Name;

                var aliasAttribute = propertyInfo.GetCustomAttribute<AliasAsAttribute>();
                if (aliasAttribute != null)
                    key = aliasAttribute.Name;

                if (DoNotConvertToQueryMap(obj))
                {
                    kvps.Add(new KeyValuePair<string, object>(key, obj));
                    continue;
                }

                switch (obj)
                {
                    case IDictionary idict:
                        foreach (var keyValuePair in BuildQueryMap(idict, delimiter))
                        {
                            kvps.Add(new KeyValuePair<string, object>($"{key}{delimiter}{keyValuePair.Key}", keyValuePair.Value));
                        }

                        break;

                    case IEnumerable ienu:
                        foreach (var o in ienu)
                        {
                            kvps.Add(new KeyValuePair<string, object>(key, o));
                        }

                        break;

                    default:
                        foreach (var keyValuePair in BuildQueryMap(obj, delimiter))
                        {
                            kvps.Add(new KeyValuePair<string, object>($"{key}{delimiter}{keyValuePair.Key}", keyValuePair.Value));
                        }

                        break;
                }
            }

            return kvps;
        }

        List<KeyValuePair<string, object>> BuildQueryMap(IDictionary dictionary, string delimiter = null)
        {
            var kvps = new List<KeyValuePair<string, object>>();

            var props = dictionary.Keys;
            foreach (string key in props)
            {
                var obj = dictionary[key];
                if (obj == null)
                    continue;

                if (DoNotConvertToQueryMap(obj))
                {
                    kvps.Add(new KeyValuePair<string, object>(key, obj));
                }
                else
                {
                    foreach (var keyValuePair in BuildQueryMap(obj, delimiter))
                    {
                        kvps.Add(new KeyValuePair<string, object>($"{key}{delimiter}{keyValuePair.Key}", keyValuePair.Value));
                    }
                }
            }

            return kvps;
        }

        Func<object[], Task<HttpRequestMessage>> BuildRequestFactoryForMethod(RestMethodInfo restMethod, string basePath, bool paramsContainsCancellationToken)
        {

            return async paramList =>
            {
                // make sure we strip out any cancelation tokens
                if (paramsContainsCancellationToken)
                {
                    paramList = paramList.Where(o => o == null || o.GetType() != typeof(CancellationToken)).ToArray();
                }

                var ret = new HttpRequestMessage
                {
                    Method = restMethod.HttpMethod
                };

                // set up multipart content
                MultipartFormDataContent multiPartContent = null;
                if (restMethod.IsMultipart)
                {
                    multiPartContent = new MultipartFormDataContent(restMethod.MultipartBoundary);
                    ret.Content = multiPartContent;
                }

                var urlTarget = (basePath == "/" ? string.Empty : basePath) + restMethod.RelativePath;
                var queryParamsToAdd = new List<KeyValuePair<string, string>>();
                var headersToAdd = new Dictionary<string, string>(restMethod.Headers);
                var queryParamShift = 0;

                for (var i = 0; i < paramList.Length; i++)
                {
                    var param = paramList[i];
                    // if part of REST resource URL, substitute it in
                    if (restMethod.ParameterMap.ContainsKey(i))
                    {
                        string pattern;
                        string replacement;
                        if (restMethod.ParameterMap[i].Item2 == ParameterType.RoundTripping)
                        {
                            pattern = $@"{{\*\*{restMethod.ParameterMap[i].Item1}}}";
                            var paramValue = param as string;
                            replacement = string.Join(
                                "/",
                                paramValue.Split('/')
                                    .Select(s =>
                                        Uri.EscapeDataString(
                                            settings.UrlParameterFormatter.Format(s, restMethod.ParameterInfoMap[i]) ?? string.Empty
                                        )
                                    )
                            );
                        }
                        else
                        {
                            pattern = "{" + restMethod.ParameterMap[i].Item1 + "}";
                            replacement = Uri.EscapeDataString(settings.UrlParameterFormatter
                                    .Format(param, restMethod.ParameterInfoMap[i]) ?? string.Empty);
                        }

                        urlTarget = Regex.Replace(
                            urlTarget,
                            pattern,
                            replacement,
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

                        queryParamShift++;

                        continue;
                    }

                    // if marked as body, add to content
                    if (restMethod.BodyParameterInfo != null && restMethod.BodyParameterInfo.Item3 == i)
                    {
                        if (param is HttpContent httpContentParam)
                        {
                            ret.Content = httpContentParam;
                        }
                        else if (param is Stream streamParam)
                        {
                            ret.Content = new StreamContent(streamParam);
                        }
                        // Default sends raw strings
                        else if (restMethod.BodyParameterInfo.Item1 == BodySerializationMethod.Default &&
                                 param is string stringParam)
                        {
                            ret.Content = new StringContent(stringParam);
                        }
                        else
                        {
                            switch (restMethod.BodyParameterInfo.Item1)
                            {
                                case BodySerializationMethod.UrlEncoded:
                                    ret.Content = param is string str ? (HttpContent)new StringContent(Uri.EscapeDataString(str), Encoding.UTF8, "application/x-www-form-urlencoded") : new FormUrlEncodedContent(new FormValueMultimap(param, settings));
                                    break;
                                case BodySerializationMethod.Default:
#pragma warning disable CS0618 // Type or member is obsolete
                                case BodySerializationMethod.Json:
#pragma warning restore CS0618 // Type or member is obsolete
                                case BodySerializationMethod.Serialized:
                                    var content = await serializer.SerializeAsync(param).ConfigureAwait(false);
                                    switch (restMethod.BodyParameterInfo.Item2)
                                    {
                                        case false:
                                            ret.Content = new PushStreamContent(
#pragma warning disable IDE1006 // Naming Styles
                                                async (stream, _, __) =>
#pragma warning restore IDE1006 // Naming Styles
                                                {
                                                    using (stream)
                                                    {
                                                        await content.CopyToAsync(stream).ConfigureAwait(false);
                                                    }
                                                }, content.Headers.ContentType);
                                            break;
                                        case true:
                                            ret.Content = content;
                                            break;
                                    }

                                    break;
                            }
                        }

                        continue;
                    }

                    // if header, add to request headers
                    if (restMethod.HeaderParameterMap.ContainsKey(i))
                    {
                        headersToAdd[restMethod.HeaderParameterMap[i]] = param?.ToString();
                        continue;
                    }

                    // ignore nulls
                    if (param == null) continue;

                    // for anything that fell through to here, if this is not
                    // a multipart method, add the parameter to the query string
                    if (!restMethod.IsMultipart)
                    {
                        var attr = restMethod.ParameterInfoMap[i].GetCustomAttribute<QueryAttribute>() ?? new QueryAttribute();
                        if (DoNotConvertToQueryMap(param))
                        {
                            if (!(param is string) && param is IEnumerable paramValues)
                            {
                                switch (attr.CollectionFormat)
                                {
                                    case CollectionFormat.Multi:
                                        foreach (var paramValue in paramValues)
                                        {
                                            queryParamsToAdd.Add(new KeyValuePair<string, string>(
                                                restMethod.QueryParameterMap.ElementAt(i - queryParamShift).Value,
                                                settings.UrlParameterFormatter.Format(paramValue, restMethod.ParameterInfoMap[i])));
                                        }
                                        continue;
                                    default:
                                        var delimiter = attr.CollectionFormat == CollectionFormat.Ssv ? " "
                                            : attr.CollectionFormat == CollectionFormat.Tsv ? "\t"
                                            : attr.CollectionFormat == CollectionFormat.Pipes ? "|"
                                            : ",";

                                        // Missing a "default" clause was preventing the collection from serializing at all, as it was hitting "continue" thus causing an off-by-one error

                                        var formattedValues = paramValues
                                            .Cast<object>()
                                            .Select(v => settings.UrlParameterFormatter.Format(v, restMethod.ParameterInfoMap[i]));

                                        queryParamsToAdd.Add(new KeyValuePair<string, string>(
                                            restMethod.QueryParameterMap.ElementAt(i - queryParamShift).Value,
                                            string.Join(delimiter, formattedValues)));
                                        continue;
                                }
                            }

                            queryParamsToAdd.Add(new KeyValuePair<string, string>(restMethod.QueryParameterMap.ElementAt(i - queryParamShift).Value, settings.UrlParameterFormatter.Format(param, restMethod.ParameterInfoMap[i])));
                        }
                        else
                        {
                            foreach (var kvp in BuildQueryMap(param, attr.Delimiter))
                            {
                                var path = !string.IsNullOrWhiteSpace(attr.Prefix) ? $"{attr.Prefix}{attr.Delimiter}{kvp.Key}" : kvp.Key;
                                queryParamsToAdd.Add(new KeyValuePair<string, string>(path, settings.UrlParameterFormatter.Format(kvp.Value, restMethod.ParameterInfoMap[i])));
                            }
                        }

                        continue;
                    }

                    // we are in a multipart method, add the part to the content
                    // the parameter name should be either the attachment name or the parameter name (as fallback)
                    string itemName;
                    string parameterName;

                    if (!restMethod.AttachmentNameMap.TryGetValue(i, out var attachment))
                    {
                        itemName = restMethod.QueryParameterMap[i];
                        parameterName = itemName;
                    }
                    else
                    {
                        itemName = attachment.Item1;
                        parameterName = attachment.Item2;
                    }

                    // Check to see if it's an IEnumerable
                    var itemValue = param;
                    var enumerable = itemValue as IEnumerable<object>;
                    var typeIsCollection = enumerable != null;

                    if (typeIsCollection)
                    {
                        foreach (var item in enumerable)
                        {
                            await AddMultipartItemAsync(multiPartContent, itemName, parameterName, item).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await AddMultipartItemAsync(multiPartContent, itemName, parameterName, itemValue).ConfigureAwait(false);
                    }
                }

                // NB: We defer setting headers until the body has been
                // added so any custom content headers don't get left out.
                if (headersToAdd.Count > 0)
                {
                    // We could have content headers, so we need to make
                    // sure we have an HttpContent object to add them to,
                    // provided the HttpClient will allow it for the method
                    if (ret.Content == null && !BodylessMethods.Contains(ret.Method))
                        ret.Content = new ByteArrayContent(new byte[0]);

                    foreach (var header in headersToAdd)
                    {
                        SetHeader(ret, header.Key, header.Value);
                    }
                }

                // NB: The URI methods in .NET are dumb. Also, we do this 
                // UriBuilder business so that we preserve any hardcoded query 
                // parameters as well as add the parameterized ones.
                var uri = new UriBuilder(new Uri(new Uri("http://api"), urlTarget));
                var query = HttpUtility.ParseQueryString(uri.Query ?? "");
                foreach (var key in query.AllKeys)
                {
                    queryParamsToAdd.Insert(0, new KeyValuePair<string, string>(key, query[key]));
                }

                if (queryParamsToAdd.Any())
                {
                    var pairs = queryParamsToAdd.Where(x => x.Key != null && x.Value != null)
                                                .Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value));
                    uri.Query = string.Join("&", pairs);
                }
                else
                {
                    uri.Query = null;
                }

                ret.RequestUri = new Uri(uri.Uri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped), UriKind.Relative);
                return ret;
            };
        }

        Func<HttpClient, object[], IObservable<T>> BuildRxFuncForMethod<T, TBody>(RestMethodInfo restMethod)
        {
            var taskFunc = BuildCancellableTaskFuncForMethod<T, TBody>(restMethod);

            return (client, paramList) =>
            {
                return new TaskToObservable<T>(ct =>
                {
                    var methodCt = CancellationToken.None;
                    if (restMethod.CancellationToken != null)
                    {
                        methodCt = paramList.OfType<CancellationToken>().FirstOrDefault();
                    }

                    // link the two
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(methodCt, ct);

                    return taskFunc(client, cts.Token, paramList);
                });
            };
        }

        Func<HttpClient, object[], Task<T>> BuildTaskFuncForMethod<T, TBody>(RestMethodInfo restMethod)
        {
            var ret = BuildCancellableTaskFuncForMethod<T, TBody>(restMethod);

            return (client, paramList) =>
            {
                if (restMethod.CancellationToken != null)
                {
                    return ret(client, paramList.OfType<CancellationToken>().FirstOrDefault(), paramList);
                }

                return ret(client, CancellationToken.None, paramList);
            };
        }

        Func<HttpClient, object[], Task> BuildVoidTaskFuncForMethod(RestMethodInfo restMethod)
        {
            return async (client, paramList) =>
            {
                if (client.BaseAddress == null)
                    throw new InvalidOperationException("BaseAddress must be set on the HttpClient instance");

                var factory = BuildRequestFactoryForMethod(restMethod, client.BaseAddress.AbsolutePath, restMethod.CancellationToken != null);
                var rq = await factory(paramList).ConfigureAwait(false);

                var ct = CancellationToken.None;

                if (restMethod.CancellationToken != null)
                {
                    ct = paramList.OfType<CancellationToken>().FirstOrDefault();
                }

                using var resp = await client.SendAsync(rq, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    throw await ApiException.Create(rq, restMethod.HttpMethod, resp, settings).ConfigureAwait(false);
                }
            };
        }

        static bool DoNotConvertToQueryMap(object value)
        {
            if (value == null)
                return false;

            var type = value.GetType();

            bool ShouldReturn() => type == typeof(string) ||
                                  type == typeof(bool) ||
                                  type == typeof(char) ||
                                  typeof(IFormattable).IsAssignableFrom(type) ||
                                  type == typeof(Uri);

            // Bail out early & match string
            if (ShouldReturn())
                return true;

            // Get the element type for enumerables
            if (value is IEnumerable enu)
            {
                var ienu = typeof(IEnumerable<>);
                // We don't want to enumerate to get the type, so we'll just look for IEnumerable<T>
                var intType = type.GetInterfaces()
                                     .FirstOrDefault(i => i.GetTypeInfo().IsGenericType &&
                                                          i.GetGenericTypeDefinition() == ienu);

                if (intType != null)
                {
                    type = intType.GetGenericArguments()[0];
                }

            }

            return ShouldReturn();
        }

        static void SetHeader(HttpRequestMessage request, string name, string value)
        {
            // Clear any existing version of this header that might be set, because
            // we want to allow removal/redefinition of headers. 
            // We also don't want to double up content headers which may have been
            // set for us automatically.

            // NB: We have to enumerate the header names to check existence because 
            // Contains throws if it's the wrong header type for the collection.
            if (request.Headers.Any(x => x.Key == name))
            {
                request.Headers.Remove(name);
            }

            if (request.Content != null && request.Content.Headers.Any(x => x.Key == name))
            {
                request.Content.Headers.Remove(name);
            }

            if (value == null) return;

            var added = request.Headers.TryAddWithoutValidation(name, value);

            // Don't even bother trying to add the header as a content header
            // if we just added it to the other collection.
            if (!added && request.Content != null)
            {
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }
}
