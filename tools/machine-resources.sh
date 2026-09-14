#!/bin/bash
# Shared resource view for every agent working on this machine.
#
# One agent cannot see another's cost. A session once drove a 16 GB machine into
# 18 GB of swap — five parallel Docker builds, node servers left over from
# earlier steps, vitest and tsc on top. Every step was cheap on its own and
# nothing held the total. A later session left a `tail -f` running for 29 hours
# because nothing ever asked whose it was.
#
# So the total lives here, and heavy work is announced before it starts.
#
#   machine-resources.sh report            what the machine is doing, and whose
#   machine-resources.sh check [gb]        exit 1 if there is not enough room
#   machine-resources.sh claim <label> <description>
#   machine-resources.sh release <label>
#   machine-resources.sh mine              what this session has claimed
#
# Claims are advisory. They exist so that an agent finding the machine short can
# tell who to talk to instead of killing something it does not understand.

set -u
REG="$HOME/.claude/machine-resources.d"
MIN_FREE_PCT=${MIN_FREE_PCT:-15}
# Free disk, in GiB. Absolute, not a percentage: this volume is 228 GiB, so 95%
# full still leaves 10 GiB and 98% full leaves 349 MiB — the same percentage
# band covers "fine" and "nothing can run". Measured on 2026-09-05: three agents
# hit ENOSPC within minutes of each other while memory read 40% free and paging
# read 12/sec, so `check` returned 0 the whole time. What they saw was not a
# slowdown — the harness could not write its own tool-output file, so commands
# died with errors that read like ordinary test failures. One agent reported
# five failing tests before noticing the cause was a full disk.
#
# The floor is set from two measured points, not from a measured threshold:
# work was healthy at 10 GiB and impossible at 0.3 GiB. 8 GiB passes today and
# still catches the slide toward zero with a build's worth of room to spare.
# Raise it if a run ever dies above the floor — that is the measurement this
# number is waiting for.
MIN_FREE_GB=${MIN_FREE_GB:-8}
# Sustained swap-ins per second. Idle-to-busy on this machine sits under 200;
# genuine thrash runs in the thousands. Set high enough that ordinary work
# passes, low enough that a machine actually fetching pages off disk is caught.
MAX_SWAPIN_RATE=${MAX_SWAPIN_RATE:-1500}
mkdir -p "$REG"

# `memory_pressure` reports the figure the kernel actually acts on. Deriving it
# from vm_stat page counts is easy to get wrong — an earlier version of this
# check read 1.1 GB free while the system reported 47%, and nearly killed a
# healthy batch on the strength of it.
free_pct () {
  memory_pressure 2>/dev/null | awk '/free percentage/{gsub(/%/,"",$NF); print $NF; found=1} END{if(!found) print 100}'
}
# $HOME, $TMPDIR and /private/tmp all live on the same data volume here, so one
# reading covers every place an agent writes.
#
# Measure $HOME, never `/`. On macOS `/` is the sealed system volume and reads
# 100% full when everything is fine — an agent that checked `df -h /` during the
# 2026-09-05 outage reported "100%" on a machine that had 15 GiB free. $HOME
# resolves to /System/Volumes/Data, which is the volume that actually fills.
free_gb () {
  df -g "$HOME" 2>/dev/null | awk 'NR==2{print $4+0}'
}
swap_used_pct () {
  sysctl -n vm.swapusage 2>/dev/null | awk '{gsub(/M/,"",$3); gsub(/M/,"",$6);
    if ($3+0 > 0) printf "%d", ($6/$3)*100; else print 0}'
}
# Swap USED is a high-water mark: macOS does not hand the file back when the
# pressure passes, so a machine that paged hard an hour ago still reads 93%
# while sitting idle. Gating on it blocks every later job on this machine
# forever, and a gate that is always red is a gate everyone learns to ignore.
# What actually says "the machine is struggling now" is the rate of swap-ins.
# Sampled over three seconds, not one. Paging here is bursty — a suite starting
# spikes it past 3000 and it falls back within a second — so a one-second sample
# of the same idle machine returned 1052 and then 2163 and then 3119, and the
# gate opened or closed on which second it happened to catch. A gate that
# answers differently each time it is asked teaches everyone to ask twice.
# The MEDIAN of four one-second samples, not the mean of one window. Paging
# here spikes by an order of magnitude for a second or two whenever anything
# starts, and a mean lets that single burst decide: four readings taken back to
# back on an idle machine were 96, 185, 237 and 1482, and the mean of a window
# containing the last one closed the gate on a machine with 49% memory free.
# It closed it on two batches at once, and neither had done anything wrong.
# A median ignores one burst and still catches sustained thrash, which is the
# only kind worth refusing work over.
swapin_rate () {
  local prev cur s
  prev=$(vm_stat 2>/dev/null | awk '/Swapins/{gsub(/\./,"",$NF); print $NF}')
  s=""
  for _ in 1 2 3 4; do
    sleep 1
    cur=$(vm_stat 2>/dev/null | awk '/Swapins/{gsub(/\./,"",$NF); print $NF}')
    s="$s$(( ${cur:-0} - ${prev:-0} ))\n"
    prev=$cur
  done
  printf "$s" | sort -n | sed -n '2p'
}

# A claim whose process is gone is stale; nobody is coming back to release it.
prune () {
  for f in "$REG"/*.claim; do
    [ -e "$f" ] || continue
    pid=$(awk -F= '/^pid=/{print $2}' "$f")
    [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null || rm -f "$f"
  done
}

case "${1:-report}" in
  report)
    prune
    printf 'memory free   %s%%   (floor %s%%)\n' "$(free_pct)" "$MIN_FREE_PCT"
    printf 'swap used     %s%%   %s   (high-water, not current pressure)\n' "$(swap_used_pct)" "$(sysctl -n vm.swapusage 2>/dev/null)"
    printf 'paging        %s swap-ins/sec   (ceiling %s)\n' "$(swapin_rate)" "$MAX_SWAPIN_RATE"
    printf 'disk free     %s GiB   (floor %s GiB)\n\n' "$(free_gb)" "$MIN_FREE_GB"

    echo 'claimed by agents:'
    n=0
    for f in "$REG"/*.claim; do
      [ -e "$f" ] || continue
      n=$((n+1))
      awk -F= '/^label=/{l=substr($0,7)} /^pid=/{p=$2} /^since=/{s=$2}
               /^what=/{w=substr($0,6)}
               END{printf "  %-18s pid %-7s %s\n      %s\n", l, p, s, w}' "$f"
    done
    [ "$n" = 0 ] && echo '  (none)'

    echo
    echo 'heaviest processes:'
    ps -Ao rss=,pid=,comm= 2>/dev/null | sort -rn | head -8 \
      | awk '{printf "  %6.2f GB  %-8s %s\n", $1/1048576, $2, $3}'

    if docker info >/dev/null 2>&1; then
      echo
      echo 'docker:'
      docker ps --format '  {{.Names}}  {{.Status}}' 2>/dev/null | head -10
    fi
    ;;

  check)
    prune
    # Disk first: a machine short on memory runs slowly, a machine out of disk
    # cannot run at all, and its failures do not name their cause.
    d=$(free_gb)
    if [ "${d:-0}" -lt "$MIN_FREE_GB" ]; then
      echo "machine has ${d} GiB free disk, wanted ${MIN_FREE_GB} GiB" >&2
      echo "This is not a slowdown. At zero the harness cannot write its own" >&2
      echo "tool output, so commands fail with ENOSPC that reads like an" >&2
      echo "ordinary test failure. Do not debug your code; free space first." >&2
      echo "claims currently held:" >&2
      for c in "$REG"/*.claim; do
        [ -e "$c" ] || continue
        awk -F= '/^label=/{l=substr($0,7)} /^pid=/{p=$2} /^what=/{w=substr($0,6)}
                 END{printf "  %s (pid %s) — %s\n", l, p, w}' "$c" >&2
      done
      exit 1
    fi
    want=${2:-$MIN_FREE_PCT}
    f=$(free_pct)
    if [ "$f" -lt "$want" ]; then
      echo "machine has ${f}% memory free, wanted ${want}%" >&2
      echo "claims currently held:" >&2
      for c in "$REG"/*.claim; do
        [ -e "$c" ] || continue
        awk -F= '/^label=/{l=substr($0,7)} /^pid=/{p=$2} /^what=/{w=substr($0,6)}
                 END{printf "  %s (pid %s) — %s\n", l, p, w}' "$c" >&2
      done
      echo "Talk to whoever holds these before killing anything." >&2
      exit 1
    fi
    r=$(swapin_rate)
    if [ "$r" -gt "$MAX_SWAPIN_RATE" ]; then
      echo "machine is paging at ${r} swap-ins/sec, ceiling ${MAX_SWAPIN_RATE}" >&2
      echo "Memory reads free, but the machine is fetching it back off disk faster" >&2
      echo "than it can use it. Wait, or find out who is holding it." >&2
      exit 1
    fi
    ;;

  claim)
    label=${2:?usage: claim <label> <description>}
    shift 2
    f="$REG/${label//\//_}.claim"
    {
      echo "label=$label"
      echo "pid=$PPID"
      echo "since=$(date '+%H:%M')"
      printf 'what=%s\n' "$(echo "$*" | tr '\n' ' ')"
    } > "$f"
    echo "claimed: $label"
    ;;

  release)
    label=${2:?usage: release <label>}
    rm -f "$REG/${label//\//_}.claim" && echo "released: $label"
    ;;

  mine)
    prune
    grep -l "pid=$PPID" "$REG"/*.claim 2>/dev/null | while read -r f; do
      awk -F= '/^label=/{l=substr($0,7)} /^what=/{w=substr($0,6)}
               END{printf "%s — %s\n", l, w}' "$f"
    done
    ;;

  *)
    echo "unknown command: $1" >&2
    exit 2
    ;;
esac
