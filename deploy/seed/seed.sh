#!/bin/sh
# Creates demo and load-test missions (idempotent: existing slugs are left alone).
set -eu
API=${API:-http://api:8080}

create() {
  code=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/missions" -H 'content-type: application/json' -d "$1")
  echo "create $(echo "$1" | sed -n 's/.*"slug":"\([^"]*\)".*/\1/p') -> $code"
}

create '{"slug":"demo","name":"Demo search","mainUrl":"http://upstream-main:8080","devUrl":"http://upstream-dev:8080","sampleRate":1,"mirrorMethods":["GET","HEAD"],"comparedHeaders":["content-type"]}'
create '{"slug":"bench-off","name":"Load test (mirroring off)","mainUrl":"http://upstream-main:8080","devUrl":"http://upstream-dev:8080","mirrorEnabled":false,"sampleRate":0}'
create '{"slug":"bench-10","name":"Load test (10% mirrored)","mainUrl":"http://upstream-main:8080","devUrl":"http://upstream-dev:8080","sampleRate":0.1,"mirrorMethods":["GET","HEAD"]}'
create '{"slug":"bench-on","name":"Load test (100% mirrored)","mainUrl":"http://upstream-main:8080","devUrl":"http://upstream-dev:8080","sampleRate":1,"mirrorMethods":["GET","HEAD"]}'

ID=$(curl -s "$API/api/missions?q=demo" | sed -n 's/.*"id":"\([^"]*\)","slug":"demo".*/\1/p')
if [ -n "$ID" ]; then
  curl -s -o /dev/null -X PUT "$API/api/missions/$ID/rules" -H 'content-type: application/json' -d '{"rules":[
    {"type":"IgnoreField","enabled":true,"config":{"paths":["$..requestId","$..took_ms"]}},
    {"type":"PrimaryKey","enabled":true,"config":{"path":"$.items","key":"sku"}},
    {"type":"SearchAndReplace","enabled":true,"config":{"scope":"body","search":"search-dev\\.internal","replace":"search.internal","regex":true}}
  ]}'
  echo "demo rules set"
fi
