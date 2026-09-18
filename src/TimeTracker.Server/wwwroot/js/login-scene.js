// Login scene behaviour: pointer/tilt parallax, the sun-clock telling the viewer's own time,
// and the two form affordances that need script (reveal password, submit busy state).
// Every piece degrades to a still, usable page if it cannot run.
(function () {
    'use strict';

    var reduceMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

    /* ----------------------------------------------------------------------
       Parallax
       Layers translate against pointer movement, scaled by their own depth.
       One rAF frame per movement batch, and the layers are inset beyond the
       viewport (see .scene-layer) so no edge is ever exposed by the travel.
       ---------------------------------------------------------------------- */
    var layers = Array.prototype.slice.call(document.querySelectorAll('.scene-layer'));

    if (layers.length && !reduceMotion) {
        var targetX = 0, targetY = 0, currentX = 0, currentY = 0, frame = null;

        var render = function () {
            // Ease toward the pointer instead of tracking it exactly: the scene settles
            // after the cursor stops, rather than snapping with it.
            currentX += (targetX - currentX) * 0.08;
            currentY += (targetY - currentY) * 0.08;

            for (var i = 0; i < layers.length; i++) {
                var depth = parseFloat(layers[i].getAttribute('data-depth')) || 0;
                layers[i].style.setProperty('--px', (currentX * depth * 60).toFixed(2) + 'px');
                layers[i].style.setProperty('--py', (currentY * depth * 38).toFixed(2) + 'px');
            }

            if (Math.abs(targetX - currentX) > 0.001 || Math.abs(targetY - currentY) > 0.001) {
                frame = window.requestAnimationFrame(render);
            } else {
                frame = null;
            }
        };

        var queue = function () {
            if (frame === null) {
                frame = window.requestAnimationFrame(render);
            }
        };

        window.addEventListener('pointermove', function (event) {
            if (event.pointerType === 'touch') {
                return;
            }
            // -1..1 from the centre of the viewport, inverted so layers drift with the light.
            targetX = -((event.clientX / window.innerWidth) * 2 - 1);
            targetY = -((event.clientY / window.innerHeight) * 2 - 1);
            queue();
        }, { passive: true });

        window.addEventListener('pointerleave', function () {
            targetX = 0;
            targetY = 0;
            queue();
        }, { passive: true });

        // Device tilt, where it is available and permitted. iOS requires an explicit
        // permission prompt that this page deliberately never asks for - the scene is
        // decorative, and a permission dialog in front of a login form is not worth it.
        if (window.DeviceOrientationEvent && typeof window.DeviceOrientationEvent.requestPermission !== 'function') {
            window.addEventListener('deviceorientation', function (event) {
                if (event.gamma === null || event.beta === null) {
                    return;
                }
                targetX = -Math.max(-1, Math.min(1, event.gamma / 35));
                targetY = -Math.max(-1, Math.min(1, (event.beta - 45) / 45));
                queue();
            }, { passive: true });
        }
    }

    /* ----------------------------------------------------------------------
       The clock keeps real time
       ---------------------------------------------------------------------- */
    var hourHand = document.getElementById('handHour');
    var minuteHand = document.getElementById('handMinute');
    var secondHand = document.getElementById('handSecond');

    if (hourHand && minuteHand && secondHand) {
        var lastSecond = -1;

        var setHands = function () {
            var now = new Date();
            var seconds = now.getSeconds();
            var minutes = now.getMinutes() + seconds / 60;
            var hours = (now.getHours() % 12) + minutes / 60;

            hourHand.style.transform = 'rotate(' + (hours * 30) + 'deg)';
            minuteHand.style.transform = 'rotate(' + (minutes * 6) + 'deg)';

            // Rotating the second hand through 360deg and back to 0 would spin it
            // backwards once a minute, so the angle accumulates instead of wrapping.
            if (seconds !== lastSecond) {
                var turns = Math.floor(now.getTime() / 60000);
                secondHand.style.transform = 'rotate(' + (turns * 360 + seconds * 6) + 'deg)';
                lastSecond = seconds;
            }
        };

        setHands();
        window.setInterval(setHands, 1000);
    }

    /* ----------------------------------------------------------------------
       Reveal password
       ---------------------------------------------------------------------- */
    var toggle = document.querySelector('[data-password-toggle]');
    var passwordInput = document.querySelector('[data-password-input]');

    if (toggle && passwordInput) {
        var shownIcon = toggle.querySelector('[data-icon="shown"]');
        var hiddenIcon = toggle.querySelector('[data-icon="hidden"]');

        toggle.addEventListener('click', function () {
            var reveal = passwordInput.type === 'password';
            passwordInput.type = reveal ? 'text' : 'password';
            toggle.setAttribute('aria-pressed', reveal ? 'true' : 'false');
            toggle.setAttribute('aria-label', reveal ? 'Hide password' : 'Show password');
            shownIcon.hidden = reveal;
            hiddenIcon.hidden = !reveal;
            // Keep the caret where the person left it.
            passwordInput.focus();
        });
    }

    /* ----------------------------------------------------------------------
       Submit state
       The round trip hits the database, so the button says it is working. It is
       never disabled: a disabled submit would swallow a retry if the POST fails.
       ---------------------------------------------------------------------- */
    var form = document.querySelector('.login-card form');
    var submit = document.querySelector('[data-submit]');

    if (form && submit) {
        form.addEventListener('submit', function () {
            // jQuery validation cancels invalid submits; only show busy once it passes.
            if (form.checkValidity && !form.checkValidity()) {
                return;
            }
            submit.setAttribute('data-busy', 'true');
        });

        // Restoring from the back/forward cache would otherwise leave it spinning.
        window.addEventListener('pageshow', function () {
            submit.removeAttribute('data-busy');
        });
    }
})();
