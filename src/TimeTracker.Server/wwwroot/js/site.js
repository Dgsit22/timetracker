// Timezone display selector: every server-rendered timestamp is UTC (the source of
// truth for filtering/storage stays UTC everywhere - see ActivityAggregation.cs). This
// only re-renders how those already-rendered UTC values are *displayed*, client-side,
// based on a choice persisted in localStorage - no server round-trip, no change to how
// date-range filters interpret "today" etc.
(function () {
    "use strict";

    var ZONES = {
        UTC: { label: "UTC", tz: "UTC" },
        IST: { label: "IST", tz: "Asia/Kolkata" },
        EST: { label: "EST", tz: "America/New_York" },
    };

    function getStoredZoneKey() {
        try {
            var stored = localStorage.getItem("tt-timezone");
            return ZONES[stored] ? stored : "UTC";
        } catch (e) {
            return "UTC";
        }
    }

    function formatInZone(isoUtc, tz, kind) {
        var date = new Date(isoUtc);
        if (isNaN(date.getTime())) {
            return null;
        }

        if (kind === "date") {
            return new Intl.DateTimeFormat("en-GB", {
                timeZone: tz, weekday: "short", day: "2-digit", month: "short", year: "numeric",
            }).format(date);
        }

        if (kind === "time") {
            return new Intl.DateTimeFormat("en-GB", {
                timeZone: tz, hour: "2-digit", minute: "2-digit", hour12: false,
            }).format(date);
        }

        var parts = new Intl.DateTimeFormat("en-CA", {
            timeZone: tz, year: "numeric", month: "2-digit", day: "2-digit",
            hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false,
        }).formatToParts(date);
        var map = {};
        parts.forEach(function (p) { map[p.type] = p.value; });
        var base = map.year + "-" + map.month + "-" + map.day + " " + map.hour + ":" + map.minute;
        return kind === "datetime-s" ? base + ":" + map.second : base;
    }

    function applyTimezone() {
        var zoneKey = getStoredZoneKey();
        var zone = ZONES[zoneKey];

        document.querySelectorAll("[data-utc]").forEach(function (el) {
            var iso = el.getAttribute("data-utc");
            var kind = el.getAttribute("data-utc-format") || "datetime";
            var formatted = formatInZone(iso, zone.tz, kind);
            if (formatted !== null) {
                el.textContent = formatted;
            }
        });

        document.querySelectorAll(".tz-label").forEach(function (el) {
            el.textContent = zone.label;
        });

        var selector = document.getElementById("tzSelector");
        if (selector) {
            selector.value = zoneKey;
        }
    }

    function setTimezone(zoneKey) {
        try {
            localStorage.setItem("tt-timezone", zoneKey);
        } catch (e) {
            // Private browsing / storage disabled: falls back to UTC every load, not fatal.
        }

        applyTimezone();
    }

    window.TimeTrackerTz = { apply: applyTimezone, set: setTimezone };

    document.addEventListener("DOMContentLoaded", function () {
        applyTimezone();
        var selector = document.getElementById("tzSelector");
        if (selector) {
            selector.addEventListener("change", function () { setTimezone(selector.value); });
        }
    });
})();
