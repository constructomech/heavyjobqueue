using HeavyJobQueue.Core;

namespace HeavyJobQueue.Core.Tests;

[TestClass]
public sealed class ProtocolTests
{
    [TestMethod]
    public void ParsesValidEnqueueMessage()
    {
        var requestId = Guid.NewGuid();
        var message = Protocol.ParseClientMessage(
            $$"""
            {"version":1,"type":"enqueue","requestId":"{{requestId:D}}","label":"Build","callerPid":42,"cwd":"C:\\src","enqueuedAt":"2026-07-21T12:00:00Z","waitTimeoutSeconds":60}
            """);

        Assert.AreEqual("enqueue", message.Type);
        Assert.AreEqual(requestId, message.RequestId);
        Assert.AreEqual("Build", message.Label);
        Assert.AreEqual(42, message.CallerPid);
        Assert.AreEqual(TimeSpan.FromMinutes(1), message.WaitTimeout);
    }

    [TestMethod]
    public void RejectsMalformedJsonWithExplicitCode()
    {
        var exception = Assert.ThrowsExactly<ProtocolException>(
            () => Protocol.ParseClientMessage("{"));

        Assert.AreEqual("malformed_json", exception.Code);
    }

    [TestMethod]
    public void RejectsIncompatibleVersionWithExplicitCode()
    {
        var exception = Assert.ThrowsExactly<ProtocolException>(
            () => Protocol.ParseClientMessage(
                """{"version":2,"type":"cancel","requestId":"57dd8a74-cff5-4c8d-b48f-902d7dd5f395"}"""));

        Assert.AreEqual("incompatible_version", exception.Code);
    }

    [TestMethod]
    public void RejectsInvalidEnqueueFields()
    {
        var exception = Assert.ThrowsExactly<ProtocolException>(
            () => Protocol.ParseClientMessage(
                """{"version":1,"type":"enqueue","requestId":"not-a-guid","label":"Build","callerPid":42,"cwd":"C:\\src","enqueuedAt":"2026-07-21T12:00:00Z","waitTimeoutSeconds":60}"""));

        Assert.AreEqual("invalid_request_id", exception.Code);
    }
}
