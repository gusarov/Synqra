#!/usr/bin/env bash
# Reports this pipeline run to a Quotaly web hook (POST /api/hooks/push), so the Synqra pull request shows up as a thing
# under the hook's node on the Quotaly graph with its status, severity and links. The thing is keyed by the GitHub pull
# request number (or the branch name for a main build); every call upserts it, so the start and the finish of a build
# land on one card.
#
# Usage: scripts/report-build-status.sh start
#        scripts/report-build-status.sh finish <Agent.JobStatus>
# Env:   QUOTALY_HOOK_SERVER (https://dev.quotaly.com), QUOTALY_HOOK_KEY (the hook's bearer token: "{WebHookId}:{secret}"),
#        plus the Azure Pipelines variables of a GitHub-sourced build (SYSTEM_PULLREQUEST_PULLREQUESTNUMBER, BUILD_BUILDID, ...).
# Never fails the pipeline: a missing key or an unreachable server is logged as a warning and the step exits 0.
set -u

phase="${1:-finish}"
detail="${2:-}"
server="${QUOTALY_HOOK_SERVER:-}"
key="${QUOTALY_HOOK_KEY:-}"

if [ -z "$server" ] || [ -z "$key" ]; then
	echo "QUOTALY_HOOK_SERVER or QUOTALY_HOOK_KEY is not set - nothing to report"
	exit 0
fi

json_escape() {
	printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' -e ':a;N;$!ba;s/\n/\\n/g'
}

collection="${SYSTEM_TEAMFOUNDATIONCOLLECTIONURI:-https://dev.azure.com/xkit/}"
project="${SYSTEM_TEAMPROJECT:-Synqra}"
repo="${BUILD_REPOSITORY_NAME:-gusarov/Synqra}"
build_url="${collection}${project}/_build/results?buildId=${BUILD_BUILDID:-0}"
sha="${BUILD_SOURCEVERSION:-}"
short_sha="${sha:0:8}"

pr="${SYSTEM_PULLREQUEST_PULLREQUESTNUMBER:-}"
if [ -n "$pr" ]; then
	pr_key="$pr"
	kind="Pull request"
	pr_url="https://github.com/${repo}/pull/${pr}"
	title="Synqra#${pr}"
	# A public repository: the title comes from the GitHub API without a token; without an answer the card says "Synqra#<n>".
	fetched=$(curl -sS -m 15 -H "Accept: application/vnd.github+json" "https://api.github.com/repos/${repo}/pulls/${pr}" 2>/dev/null | sed -n 's/^  "title": "\(.*\)",$/\1/p' | head -1)
	if [ -n "$fetched" ]; then
		title="Synqra#${pr}: ${fetched}"
	fi
else
	branch="${BUILD_SOURCEBRANCHNAME:-main}"
	pr_key="$branch"
	kind="Branch"
	pr_url="https://github.com/${repo}/tree/${branch}"
	title="Synqra ${branch}"
fi

subtitle="build ${BUILD_BUILDNUMBER:-?} · ${short_sha}"
urls="{\"kind\":\"pull-request\",\"url\":\"$(json_escape "$pr_url")\",\"label\":\"$(json_escape "$title")\"},{\"kind\":\"build\",\"url\":\"$(json_escape "$build_url")\",\"label\":\"build ${BUILD_BUILDNUMBER:-?}\"}"

case "$phase" in
	start)
		status="building"
		severity="info"
		;;
	finish)
		case "$detail" in
			Succeeded) status="build succeeded"; severity="none" ;;
			SucceededWithIssues) status="build succeeded with issues"; severity="warn" ;;
			Canceled) status="build canceled"; severity="warn" ;;
			*) status="build failed"; severity="critical" ;;
		esac
		;;
	*)
		echo "##vso[task.logissue type=warning]report-build-status: unknown phase '$phase'"
		exit 0
		;;
esac

body="{\"key\":\"$(json_escape "$pr_key")\",\"title\":\"$(json_escape "$title")\",\"subtitle\":\"$(json_escape "$subtitle")\",\"kind\":\"${kind}\",\"status\":\"${status}\",\"severity\":\"${severity}\",\"urls\":[${urls}]}"

out=$(mktemp)
code=$(curl -sS -m 30 -o "$out" -w '%{http_code}' -X POST -H "Authorization: Bearer ${key}" -H "Content-Type: application/json" --data "$body" "${server%/}/api/hooks/push" 2>"$out.err")
echo "report-build-status: ${phase} '${pr_key}' = ${status} -> ${server%/}/api/hooks/push : HTTP ${code} $(head -c 300 "$out") $(head -c 300 "$out.err")"
case "$code" in
	2*) ;;
	*) echo "##vso[task.logissue type=warning]report-build-status: the hook answered HTTP ${code}" ;;
esac
rm -f "$out" "$out.err"
exit 0
