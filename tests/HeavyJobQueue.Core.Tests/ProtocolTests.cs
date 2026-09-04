using HeavyJobQueue.Core;

namespace HeavyJobQueue.Core.Tests;

[TestClass]
public sealed class ProtocolTests
{
    [TestMethod]
    public void ParsesValidEnqueueMessage()
    {
        var requestId = Guid.NewGuid();
        var message = Protocol.ParseClientMessage(Protocol.Serialize(new
        {
            version = Protocol.Version,
            type = "enqueue",
            requestId,
            label = "Build",
            callerPid = 42,
            cwd = @"C:\src",
            command = "dotnet build",
            accessMode = "shared",
            enqueuedAt = "2026-07-21T12:00:00Z",
            waitTimeoutSeconds = 60,
            leaseName = RequestLease.GetName(requestId)
        }));

        Assert.AreEqual("enqueue", message.Type);
        Assert.AreEqual(requestId, message.RequestId);
        Assert.AreEqual("Build", message.Label);
        Assert.AreEqual(42, message.CallerPid);
        Assert.AreEqual("dotnet build", message.Command);
        Assert.AreEqual(JobAccessMode.Shared, message.AccessMode);
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
    public void AcceptsEnqueueMessagesWithoutCommand()
    {
        var requestId = Guid.NewGuid();
        var message = Protocol.ParseClientMessage(Protocol.Serialize(new
        {
            version = Protocol.Version,
            type = "enqueue",
            requestId,
            label = "Build",
            callerPid = 42,
            cwd = @"C:\src",
            enqueuedAt = "2026-07-21T12:00:00Z",
            waitTimeoutSeconds = 60,
            leaseName = RequestLease.GetName(requestId)
        }));

        Assert.IsNull(message.Command);
        Assert.AreEqual(JobAccessMode.Exclusive, message.AccessMode);
    }

    [TestMethod]
    public void RejectsInvalidAccessMode()
    {
        var requestId = Guid.NewGuid();
        var exception = Assert.ThrowsExactly<ProtocolException>(
            () => Protocol.ParseClientMessage(Protocol.Serialize(new
            {
                version = Protocol.Version,
                type = "enqueue",
                requestId,
                label = "Build",
                callerPid = 42,
                cwd = @"C:\src",
                accessMode = "sometimes",
                enqueuedAt = "2026-07-21T12:00:00Z",
                waitTimeoutSeconds = 60,
                leaseName = RequestLease.GetName(requestId)
            })));

        Assert.AreEqual("invalid_access_mode", exception.Code);
    }

    [TestMethod]
    public void RejectsIncompatibleVersionWithExplicitCode()
    {
        var exception = Assert.ThrowsExactly<ProtocolException>(
            () => Protocol.ParseClientMessage(
                """{"version":1,"type":"cancel","requestId":"57dd8a74-cff5-4c8d-b48f-902d7dd5f395"}"""));

        Assert.AreEqual("incompatible_version", exception.Code);
    }

    [TestMethod]
    public void RejectsInvalidEnqueueFields()
    {
        var exception = Assert.ThrowsExactly<ProtocolException>(
            () => Protocol.ParseClientMessage(
                """{"version":2,"type":"enqueue","requestId":"not-a-guid","label":"Build","callerPid":42,"cwd":"C:\\src","enqueuedAt":"2026-07-21T12:00:00Z","waitTimeoutSeconds":60,"leaseName":"invalid"}"""));

        Assert.AreEqual("invalid_request_id", exception.Code);
    }
}
