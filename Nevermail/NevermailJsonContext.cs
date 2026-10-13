using System.Text.Json;
using System.Text.Json.Serialization;
using Nevermail.Mail;

namespace Nevermail;

// Source-generated metadata for everything the UI API returns and the file store persists.
// Reflection is disabled in the AOT build (IlcDisableReflection), so every serialized type must be listed here.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(StoredMail))]
[JsonSerializable(typeof(List<MailSummary>))]
[JsonSerializable(typeof(Nevermail.Api.NevermailInfo))]
public sealed partial class NevermailJsonContext : JsonSerializerContext;
