#pragma once
#include "system.h"
namespace magic::system
{
class Metrics
{
    struct Impl;
    std::unique_ptr<Impl> impl;

  public:
    Metrics();
    ~Metrics();
    void Configure(bool gpu, bool storage);
    JsonObject Sample();
};
} // namespace magic::system
