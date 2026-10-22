using EchoDiar.Core.Model;
using EchoDiar.Core.Settings;

namespace EchoDiar.Core;

/// <summary>
/// Swift <c>RetentionPolicy</c>: which conversations auto-delete moves to the Recycle Bin. Pure: it deletes nothing
/// (<see cref="LibraryStore"/> does) and ignores <see cref="AppSettings.AutoDeleteEnabled"/>, so the Storage page can
/// preview before the user turns auto-delete on.
/// </summary>
public static class RetentionPolicy
{
    /// <summary>
    /// What goes to the Recycle Bin: the conversation folder (whole conversation) or its audio files (audio only).
    /// </summary>
    public sealed record Deletion(Guid ConversationId, RetentionMode Mode, IReadOnlyList<string> Items);

    /// <summary>
    /// Transcribed conversations created more than <see cref="AppSettings.AutoDeleteAfterDays"/> days before
    /// <paramref name="now"/>. Never selects a conversation that is recording, compressing, transcribing, failed or not
    /// yet transcribed. Audio only: lists just the conversation's <paramref name="audioFiles"/> (default
    /// <see cref="LibraryStore.AudioFiles"/>), and skips it when it has none left.
    /// </summary>
    public static IReadOnlyList<Deletion> Select(
        IEnumerable<Conversation> conversations, DateTimeOffset now, AppSettings settings,
        Func<Conversation, IReadOnlyList<string>>? audioFiles = null)
    {
        audioFiles ??= c => LibraryStore.AudioFiles(c.Folder);
        var days = AppSettings.Clamp(settings.AutoDeleteAfterDays, AppSettings.AutoDeleteDaysRange);
        var cutoff = now.AddSeconds(-(double)days * 86_400);
        List<Deletion> deletions = [];
        foreach (var conversation in conversations)
        {
            if (conversation.Status != ConversationStatus.Transcribed || !conversation.HasTranscript
                || conversation.CreatedAt >= cutoff)
            {
                continue;
            }
            switch (settings.AutoDeleteMode)
            {
                case RetentionMode.WholeConversation:
                    deletions.Add(new Deletion(conversation.Id, RetentionMode.WholeConversation, [conversation.Folder]));
                    break;
                case RetentionMode.AudioOnly:
                    var audio = audioFiles(conversation);
                    if (audio.Count > 0) deletions.Add(new Deletion(conversation.Id, RetentionMode.AudioOnly, audio));
                    break;
            }
        }
        return deletions;
    }
}
