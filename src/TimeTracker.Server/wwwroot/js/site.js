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

        // The Activity page's time-of-day window is evaluated server-side, and this choice lives
        // only in localStorage, so the IANA id has to travel with the form for "09:00" to mean
        // 09:00 in the zone on screen rather than 09:00 UTC.
        document.querySelectorAll("[data-timezone-field]").forEach(function (field) {
            field.value = zone.tz;
        });
    }

    function setTimezone(zoneKey) {
        try {
            localStorage.setItem("tt-timezone", zoneKey);
        } catch (e) {
            // Private browsing / storage disabled: falls back to UTC every load, not fatal.
        }

        applyTimezone();
        document.dispatchEvent(new CustomEvent("tt-timezone-change"));

        // A time-of-day window was filtered in the previous zone, so the totals on screen no
        // longer mean what the inputs now say. Re-run the query rather than leave the two
        // disagreeing - only when such a window is actually in play.
        var zoneField = document.querySelector("[data-timezone-field]");
        var windowStart = document.getElementById("TimeFrom");
        var windowEnd = document.getElementById("TimeTo");
        if (zoneField && windowStart && windowEnd && windowStart.value && windowEnd.value) {
            zoneField.form.submit();
        }
    }

    // zone() lets pages that draw time themselves (the Activity timeline) follow the same choice,
    // re-rendering on the tt-timezone-change event.
    window.TimeTrackerTz = {
        apply: applyTimezone,
        set: setTimezone,
        zone: function () { return ZONES[getStoredZoneKey()]; },
    };

    document.addEventListener("DOMContentLoaded", function () {
        applyTimezone();
        var selector = document.getElementById("tzSelector");
        if (selector) {
            selector.addEventListener("change", function () { setTimezone(selector.value); });
        }
    });
})();
