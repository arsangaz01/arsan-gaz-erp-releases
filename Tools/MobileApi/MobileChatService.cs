using ArsanGazERP.Services;

namespace ArsanGazERP.MobileApi;

internal sealed class MobileChatService
{
    private const int MaximumHistoryTurns = 12;
    private readonly AgentChatService _chat = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<ErpAgentReply>> SendAsync(
        string message,
        IReadOnlyList<ErpChatTurn> history,
        CancellationToken cancellationToken)
    {
        List<ErpChatTurn> recent = history
            .Where(turn => turn.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(turn.Content))
            .TakeLast(MaximumHistoryTurns)
            .Select(turn => new ErpChatTurn(turn.Role, turn.Content.Length > 2000 ? turn.Content[..2000] : turn.Content))
            .ToList();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            MobileLog.Write("Mobil sohbet mesajı alındı (" + message.Trim().Length + " karakter).");
            IReadOnlyList<ErpAgentReply> replies = await _chat.RespondToAllAsync(recent, message.Trim(), cancellationToken);
            return replies.Where(reply => !reply.IsProgress).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }
}
