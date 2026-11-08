using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace MagicCommunity;
internal sealed partial class NativeWeather
{
    private sealed record WeatherFailure(string Code,string Message,int? HttpStatus=null,DateTimeOffset? RetryAfter=null);
    private sealed class WeatherRequestFailure(WeatherFailure detail):IOException(detail.Message){internal WeatherFailure Detail=>detail;}
    private static WeatherFailure HttpFailure(int status,DateTimeOffset? retry)=>status switch{
        429=>new("rate_limited","天气服务返回限流（HTTP 429），请按提示的等待时间重试。",status,retry),
        403=>new("http_forbidden","天气接口返回 HTTP 403，请检查网络访问或代理设置。",status),
        >=500=>new("service_error",$"天气接口暂时不可用（HTTP {status}）。",status),
        _=>new("http_error",$"天气接口返回 HTTP {status}，本次未取得数据。",status)};
    private static WeatherFailure Explain(Exception error)=>error switch{
        WeatherRequestFailure request=>request.Detail,
        HttpRequestException request=>request.HttpRequestError switch{
            HttpRequestError.NameResolutionError=>new("dns","无法解析天气服务地址（DNS），请检查网络或代理设置。"),
            HttpRequestError.SecureConnectionError=>new("tls","天气服务的安全连接失败（TLS），请检查网络或代理证书设置。"),
            HttpRequestError.ProxyTunnelError=>new("proxy","代理连接失败，请检查代理是否已启动及其网络连接。"),
            HttpRequestError.ConnectionError=>new("connection","天气网络连接失败或被拒绝，请检查联网状态及代理是否已启动。"),
            _=>new("network","天气网络请求失败，请检查网络或代理连接。")},
        OperationCanceledException=>new("timeout","天气请求超时，请检查网络或代理连接。"),
        JsonException or KeyNotFoundException or InvalidOperationException=>new("invalid_data","天气接口返回的数据不完整或格式无效。"),
        _=>new("read_error","天气响应读取失败，本次未取得完整数据。")};
}
