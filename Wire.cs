using System.Globalization;
using System.Text.Json.Nodes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TdLib;

namespace TgCli;

internal static class Wire
{
    internal const string Telegram = "telegram";
    internal const string LocalCache = "local_cache";

    internal static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal static JValue NullableId(long? value) => value is long id ? new JValue(Id(id)) : JValue.CreateNull();

    internal static string Timestamp(int unixSeconds) => Timestamp(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

    internal static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    internal static JsonObject ToJsonObject(JObject value) => JsonNode.Parse(value.ToString(Formatting.None))!.AsObject();

    internal static JObject Link(MessageLinkInfo link) => new()
    {
        ["chat_id"] = Id(link.ChatId),
        ["message_id"] = Id(link.MessageId),
        ["short_message_id"] = NullableId(link.ShortMessageId > 0 ? link.ShortMessageId : null),
        ["tg_url"] = link.TgUrl,
        ["tg_url_scope"] = link.TgUrlScope,
        ["https_url"] = link.HttpsUrl,
        ["https_fallback"] = link.HttpsFallback
    };

    internal static MessageLinkInfo LocalLink(long chatId, long messageId) =>
        new(chatId, messageId, ChatHistory.GetShortMessageId(messageId), MessageLinks.GetTgUrl(chatId, messageId), null, null);

    internal static JObject Topic(TdApi.ForumTopic topic) => new()
    {
        ["chat_id"] = Id(topic.Info.ChatId),
        ["topic_id"] = Id(topic.Info.ForumTopicId),
        ["name"] = topic.Info.Name,
        ["is_general"] = topic.Info.IsGeneral,
        ["is_closed"] = topic.Info.IsClosed,
        ["is_hidden"] = topic.Info.IsHidden,
        ["is_pinned"] = topic.IsPinned,
        ["unread_count"] = topic.UnreadCount,
        ["last_message_id"] = NullableId(topic.LastMessage?.Id)
    };

    internal static JObject Coverage(string source, bool complete, string terminationReason, object? requestedScope = null,
        string? continuation = null, int? pagesFetched = null, int? rowsFetched = null) => new()
    {
        ["source"] = source,
        ["requested_scope"] = requestedScope is null ? JValue.CreateNull() : JToken.FromObject(requestedScope),
        ["complete"] = complete,
        ["termination_reason"] = terminationReason,
        ["continuation"] = continuation,
        ["pages_fetched"] = pagesFetched,
        ["rows_fetched"] = rowsFetched
    };

    internal static JObject ContextCoverage(ContextReadResult result) => new()
    {
        ["window_complete"] = result.WindowComplete,
        ["older_complete"] = result.OlderComplete,
        ["newer_complete"] = result.NewerComplete,
        ["older_count"] = result.OlderCount,
        ["newer_count"] = result.NewerCount,
        ["reply_chain_status"] = result.ReplyChainStatus,
        ["unavailable_ancestor"] = result.UnavailableAncestor is { } ancestor ? new JObject
        {
            ["chat_id"] = Id(ancestor.ChatId),
            ["message_id"] = Id(ancestor.MessageId)
        } : JValue.CreateNull()
    };

    internal static JObject Stats(JObject value)
    {
        var result = (JObject)value.DeepClone();
        result["chat_id"] = NullableId(value.Value<long?>("chat_id"));
        result["topic_id"] = NullableId(value.Value<long?>("topic_id"));
        result["migrations"] = new JArray(value["migrations"]!.Values<long>().Select(Id));
        if (value.Value<string>("source") == "cache") result["source"] = LocalCache;
        return result;
    }

    // TDLib's rich details have their own field names. Enumerate identity fields and leave counters,
    // dimensions, timestamps and other numeric data in their native representation.
    private static readonly HashSet<string> NativeIdFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "animationid", "basicgroupid", "boostedchatid", "botuserid", "buttonid", "businessconnectionid",
        "channelid", "chatid", "checklistmessageid", "creatorid", "customemojiid", "directmessageschattopicid",
        "editiconcustomemojiid", "fileid", "fileuniqueid", "forumtopicid", "fromuserid", "gameid", "gamemessageid",
        "gifteruserid", "giveawaymessageid", "groupcallid", "hostid", "iconcustomemojiid", "invoicechatid", "invoicemessageid",
        "lastmessageid", "lastreadinboxmessageid", "lastreadoutboxmessageid", "messagethreadid", "messageid", "newowneruserid",
        "offermessageid", "oldbackgroundmessageid", "optionid", "ownerid", "pollmessageid", "providerpaymentchargeid",
        "publisherchatid", "receiptmessageid", "receivedgiftid", "receiverid", "receiveruserid", "regulargiftid", "remoteid",
        "requestmessageid", "savedmessagestopicid", "senderchatid", "senderid", "senderuserid", "shippingoptionid",
        "stickerid", "stickersetid", "storyid", "storyposterchatid", "suggestedpostmessageid", "supergroupid", "taskid",
        "telegrampaymentchargeid", "themeid", "topicid", "transactionid", "travelerid", "uniqueid", "upgradedreceivedgiftid",
        "usedthemechatid", "userid", "videochatid", "watcherid", "webappid"
    };

    private static readonly HashSet<string> NativeIdArrayFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "chatids", "customemojiids", "markedasdonetaskids", "markedasnotdonetaskids", "memberuserids", "messageids",
        "otherparticipantids", "recentreplierids", "recentvoterids", "taskids", "userids", "winneruserids"
    };

    internal static JToken NativeDetails(object? value)
    {
        if (value is null) return JValue.CreateNull();
        var result = JToken.FromObject(value, JsonSerializer.CreateDefault());
        if (result is not JContainer container) return result;
        foreach (var property in container.Descendants().OfType<JProperty>().ToArray())
        {
            var field = property.Name.Replace("_", "", StringComparison.Ordinal);
            if (NativeIdFields.Contains(field) && property.Value.Type == JTokenType.Integer)
                property.Value = Id(property.Value.Value<long>());
            if (NativeIdArrayFields.Contains(field) && property.Value is JArray ids)
                for (var i = 0; i < ids.Count; i++)
                    if (ids[i].Type == JTokenType.Integer) ids[i] = Id(ids[i].Value<long>());
        }
        return result;
    }
}
