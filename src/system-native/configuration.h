#pragma once
#include "system.h"
namespace magic::system
{
class ConfigurationProvider
{
    std::wstring directory;
    std::shared_ptr<ChangeState> state;
    std::wstring File() const;
    void VerifyDirectory() const;
    JsonObject Load() const;

  public:
    ConfigurationProvider(std::wstring, std::shared_ptr<ChangeState>);
    JsonObject Read() const;
    JsonObject LaunchTargets() const;
    JsonObject Write(JsonObject const &);
    static JsonObject Catalog();
    static void Validate(JsonObject const &);
};
} // namespace magic::system
