namespace PromptFloat.Services;

public interface ISecretStore
{
    void Save(string id, string secret);
    string? Read(string id);
    bool Exists(string id);
    void Delete(string id);
}
