using MolaGPT.Core.Models;
using MolaGPT.Storage;

namespace MolaGPT.ViewModels;

public sealed partial class PersonaListViewModel
{
    private AttachmentStore? _roleAssets;
    private AttachmentStore RoleAssets => _roleAssets ??= new AttachmentStore(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MolaGPT", "role-assets"));

    public byte[]? ReadCardSource(PersonaItemViewModel item) =>
        item.PendingCardSource ?? ReadRoleAsset(item.Profile.SourceCardFile);

    public byte[]? ReadAvatarSource(PersonaItemViewModel item) =>
        item.PendingAvatarSource ?? ReadRoleAsset(item.Profile.AvatarSourceFile);

    private byte[]? ReadRoleAsset(string? name)
    {
        if (name is null) return null;
        if (!RoleAssets.TryGetPath(name, out var path)) throw new FileNotFoundException("角色卡的原始文件不存在。", name);
        return File.ReadAllBytes(path);
    }

    private void PersistCardAssets(PersonaItemViewModel item)
    {
        if (item.PendingCardSource is { } source)
            item.Profile.SourceCardFile = RoleAssets.Save(source, null, item.PendingCardFileName);
        if (item.PendingAvatarSource is { } avatar)
            item.Profile.AvatarSourceFile = RoleAssets.Save(avatar, null, item.PendingAvatarFileName);
    }

    public byte[] ExportCardJson(PersonaItemViewModel item, string? exportSpec = null) =>
        CharacterCardWriter.WriteJson(item.Name, item.SystemPrompt, item.Profile, ReadCardSource(item), exportSpec);

    public void ApplyImportedCard(PersonaItemViewModel target, ImportedCharacter imported,
        byte[] source, string fileName, string? avatar, bool preservePreferences)
    {
        if (target.IsBuiltin) throw new InvalidOperationException("请先复制内置角色。");
        var profile = imported.Profile;
        byte[]? retainedAvatar = null;
        if (preservePreferences)
        {
            var old = target.Profile;
            profile.Mode = target.Mode;
            profile.Compatibility = old.Compatibility;
            profile.ProviderId = old.ProviderId;
            profile.ModelId = old.ModelId;
            profile.Temperature = old.Temperature;
            profile.TopP = old.TopP;
            profile.MaxTokens = old.MaxTokens;
            profile.EnablePython = old.EnablePython;
            profile.EnableFileTools = old.EnableFileTools;
            profile.EnableImageGeneration = old.EnableImageGeneration;
            profile.EnableMcp = old.EnableMcp;
            profile.UserName = old.UserName;
            profile.UserDescription = old.UserDescription;
            profile.UserPersonaId = old.UserPersonaId;
            profile.SharedLorebookIds = old.SharedLorebookIds.ToList();
            profile.LoreBudget = old.LoreBudget;
            if (avatar is null)
            {
                profile.AvatarSourceFile = old.AvatarSourceFile;
                profile.CardAvatarChanged = old.CardAvatarChanged;
                retainedAvatar = ReadAvatarSource(target)
                    ?? (ReadCardSource(target) is { } previous ? CharacterCardReader.Read(previous).Avatar : null);
            }
        }
        target.Name = imported.Name;
        target.SystemPrompt = imported.SystemPrompt;
        target.Profile = profile;
        if (avatar is not null) target.Avatar = avatar;
        target.PendingCardSource = source;
        target.PendingCardFileName = fileName;
        target.PendingAvatarSource = retainedAvatar;
        target.PendingAvatarFileName = retainedAvatar is null ? null : "avatar.png";
        target.RefreshProfile();
    }
}

public sealed partial class PersonaItemViewModel
{
    public byte[]? PendingCardSource { get; set; }
    public string? PendingCardFileName { get; set; }
    public byte[]? PendingAvatarSource { get; set; }
    public string? PendingAvatarFileName { get; set; }
    public string TagsText
    {
        get => string.Join("，", Profile.Tags);
        set
        {
            if (value == TagsText) return;
            Profile.Tags = value.Split([',', '，'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            OnPropertyChanged();
        }
    }
}
