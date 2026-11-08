#pragma once
#include <algorithm>
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace magic::system {
struct NotificationIdentity {
    uint32_t id;
    int64_t created;
    std::wstring app;
    bool operator==(NotificationIdentity const &) const = default;
};
struct NotificationLookup { std::optional<NotificationIdentity> value; int32_t error = 0; };
struct NotificationRemoval { size_t removed = 0, remaining = 0; int32_t error = 0; };

// Remove only the identities present when the user clicked. A recycled Windows
// ID must not delete a new notification, and a raced dismissal is success only
// after the final snapshot proves the original identity is gone.
template<class Lookup, class Remove, class Snapshot>
NotificationRemoval RemoveNotificationSnapshot(std::vector<NotificationIdentity> const &targets,
                                               Lookup lookup, Remove remove, Snapshot snapshot) {
    NotificationRemoval result;
    for (auto const &target : targets) {
        auto current = lookup(target.id);
        if (current.error) { result.error = current.error; continue; }
        if (!current.value || *current.value != target) continue;
        if (auto error = remove(target.id)) result.error = error;
    }
    auto remaining = snapshot(); // Do not manufacture an empty list on failure.
    for (auto const &target : targets)
        if (std::find(remaining.begin(), remaining.end(), target) != remaining.end()) ++result.remaining;
    result.removed = targets.size() - result.remaining;
    if (!result.remaining) result.error = 0;
    return result;
}
}
