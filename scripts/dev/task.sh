#!/usr/bin/env bash
# Dev helper for phanes-task. Usage: scripts/dev/task.sh <command>
#
#   infra   start shared dev infra (Kafka/MySQL/Redis/Mongo) via deploy/infra.dev.yml
#   build   dotnet build the Task solution
#   test    run the Task test suite
#   run     run the host in Development (Kafka localhost:9092, Redis localhost:6379)
#           — a Debug build, so the VS Code ".NET Attach" debugger can attach any time
#   smoke   exercise the running host: echo / countdown / flaky / slow+kill, print statuses
#   stop    SIGTERM the running host (graceful: actors drain, offsets committed)
#   lag     show the phanes-task consumer group's committed offset and lag
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
TASK="$ROOT/services/phanes-task"
PORT="${PHANES_TASK_PORT:-5199}"
BASE="http://localhost:$PORT"

cmd="${1:-help}"
case "$cmd" in
  infra)
    docker compose -f "$ROOT/deploy/infra.dev.yml" up -d
    ;;
  build)
    dotnet build "$TASK/Phanes.Task.sln" --nologo -v q
    ;;
  test)
    dotnet test "$TASK/Phanes.Task.sln" --nologo -v q
    ;;
  run)
    cd "$TASK/src/Phanes.Task.Host"
    echo "phanes-task → $BASE  (Ctrl-C = graceful stop)"
    ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="$BASE" exec dotnet run
    ;;
  smoke)
    j() { curl -s -X POST "$BASE/tasks/init" -H 'content-type: application/json' -d "$1"; echo; }
    echo "== task types"; curl -s "$BASE/task-types"; echo
    run="$(date +%s)"
    j "{\"taskType\":\"phanes.echo\",\"taskId\":\"echo-$run\",\"content\":\"hello\"}"
    j "{\"taskType\":\"phanes.countdown\",\"taskId\":\"cd-$run\",\"content\":\"3\",\"context\":{\"step_delay_ms\":\"200\"}}"
    j "{\"taskType\":\"phanes.flaky\",\"taskId\":\"flaky-$run\",\"eventId\":\"f1\",\"content\":\"fail\"}"
    j "{\"taskType\":\"phanes.flaky\",\"taskId\":\"flaky-$run\",\"eventId\":\"f2\",\"content\":\"throw\"}"
    j "{\"taskType\":\"phanes.slow\",\"taskId\":\"slow-$run\",\"content\":\"20000\"}"
    sleep 2
    curl -s -X POST "$BASE/tasks/phanes.slow/slow-$run/kill" -o /dev/null -w "kill → HTTP %{http_code}\n"
    sleep 4
    for t in "phanes.echo/echo-$run" "phanes.countdown/cd-$run" "phanes.flaky/flaky-$run" "phanes.slow/slow-$run"; do
      echo "== $t"; curl -s "$BASE/tasks/$t/status" | python3 -m json.tool
    done
    echo "== watermark"; curl -s "$BASE/watermark"; echo
    ;;
  stop)
    pkill -TERM -f 'Phanes.Task.Host' && echo "SIGTERM sent" || echo "not running"
    ;;
  lag)
    docker exec phanes-kafka /opt/kafka/bin/kafka-consumer-groups.sh \
      --bootstrap-server localhost:9092 --describe --group phanes-task
    ;;
  *)
    sed -n '2,12p' "$0"
    ;;
esac
