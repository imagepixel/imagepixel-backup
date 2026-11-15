# Starts the keyring of GNOME, unlocked with a password of its own, and waits until
# it answers. To be sourced by a shell that runs inside a session bus:
#
#   dbus-run-session -- bash -c '. tests/start-keyring.sh; ...'
#
# The keyring starts in two steps, as it does at a login: the first unlocks it, the
# second connects it to the session bus. Whoever asks the bus for a secret before
# that is done makes the bus start a second keyring, which reads the file of the
# first while it is still being written and then knows no keyring at all.
#
# Requirements: gnome-keyring and dbus-send, which comes with dbus.

keyring_control="$(echo -n "test" | gnome-keyring-daemon --unlock --components=secrets \
    | sed -n 's/^GNOME_KEYRING_CONTROL=//p')"
if [ -n "$keyring_control" ]; then
    # where the second step finds the keyring, if it is not at its usual place
    export GNOME_KEYRING_CONTROL="$keyring_control"
fi

keyring_socket="${GNOME_KEYRING_CONTROL:-${XDG_RUNTIME_DIR:-/nonexistent}/keyring}/control"
for _ in $(seq 100); do
    if [ -S "$keyring_socket" ]; then
        break
    fi
    sleep 0.1
done

gnome-keyring-daemon --start --components=secrets > /dev/null

keyring_answers() {
    dbus-send --session --dest=org.freedesktop.DBus --print-reply /org/freedesktop/DBus \
        org.freedesktop.DBus.NameHasOwner string:org.freedesktop.secrets 2> /dev/null \
        | grep -q "boolean true"
}

for _ in $(seq 100); do
    if keyring_answers; then
        break
    fi
    sleep 0.1
done

if ! keyring_answers; then
    echo "The keyring did not start." >&2
    exit 1
fi
