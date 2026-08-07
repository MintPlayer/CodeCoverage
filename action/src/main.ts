import * as core from '@actions/core';
import * as exec from '@actions/exec';
import * as fs from 'fs';
import * as path from 'path';
import * as zlib from 'zlib';
import { collectContext } from './context';
import { findCoverageFiles } from './files';

const MAX_RETRIES = 3;

// getBooleanInput throws on empty input; action.yml defaults cover GitHub
// runs, but be robust for local/other runners: absent means false.
function getBool(name: string): boolean {
  return core.getInput(name).trim().toLowerCase() === 'true';
}

async function run(): Promise<void> {
  const failCiIfError = getBool('fail-ci-if-error');

  try {
    const url = core.getInput('url', { required: true }).replace(/\/+$/, '');
    const token = core.getInput('token', { required: true });
    const ctx = collectContext();

    const files = await findCoverageFiles(
      core.getInput('files') || undefined,
      core.getInput('directory') || undefined,
      ctx.rootDir,
      getBool('disable-search'),
    );

    if (files.length === 0) {
      throw new Error(
        'No coverage report files found. Pass `files:` explicitly or check that your test step writes lcov/cobertura output.',
      );
    }
    core.info(`Uploading ${files.length} coverage file(s) for ${ctx.repository}@${ctx.commitSha}:`);
    for (const file of files) {
      core.info(`  ${path.relative(ctx.rootDir, file)}`);
    }

    const fileList = await gitLsFiles(ctx.rootDir);

    const form = new FormData();
    form.set('repository', ctx.repository);
    form.set('commitSha', ctx.commitSha);
    if (ctx.branch) form.set('branch', ctx.branch);
    if (ctx.pullRequestNumber) form.set('pullRequestNumber', String(ctx.pullRequestNumber));
    if (ctx.parentSha) form.set('parentSha', ctx.parentSha);
    form.set('runId', String(ctx.runId));
    form.set('runAttempt', String(ctx.runAttempt));
    form.set('jobName', core.getInput('name') || ctx.jobName);
    form.set('workflow', ctx.workflow);
    form.set('eventName', ctx.eventName);
    const flags = core.getInput('flags');
    if (flags) form.set('flags', flags);
    form.set('rootDir', ctx.rootDir);
    if (fileList) form.set('fileList', fileList);

    for (const file of files) {
      // Server ungzips by magic bytes; gzip keeps multi-MB lcov payloads small.
      const gzipped = zlib.gzipSync(fs.readFileSync(file));
      form.append('files', new Blob([new Uint8Array(gzipped)]), path.basename(file) + '.gz');
    }

    const response = await postWithRetry(`${url}/api/uploads`, token, form);
    const body = (await response.json()) as { buildId: string; sessionId: string };
    core.info(`Upload accepted: build ${body.buildId}, session ${body.sessionId}`);
    core.setOutput('build-id', body.buildId);
    core.setOutput('session-id', body.sessionId);

    if (getBool('finish')) {
      const finish = await postWithRetry(
        `${url}/api/uploads/finish`,
        token,
        JSON.stringify({
          repository: ctx.repository,
          commitSha: ctx.commitSha,
          runId: ctx.runId,
          runAttempt: ctx.runAttempt,
        }),
        'application/json',
      );
      core.info(`Finish requested (${finish.status}) — the build finalizes once parsing completes.`);
    }
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    if (failCiIfError) {
      core.setFailed(message);
    } else {
      core.warning(`Coverage upload failed (not failing CI): ${message}`);
    }
  }
}

async function gitLsFiles(cwd: string): Promise<string | null> {
  try {
    const output = await exec.getExecOutput('git', ['ls-files'], { cwd, silent: true });
    return output.exitCode === 0 ? output.stdout : null;
  } catch {
    core.warning('git ls-files failed — path matching on the server will be best-effort.');
    return null;
  }
}

async function postWithRetry(
  url: string,
  token: string,
  body: FormData | string,
  contentType?: string,
): Promise<Response> {
  let lastError: unknown;
  for (let attempt = 1; attempt <= MAX_RETRIES; attempt++) {
    try {
      const headers: Record<string, string> = { Authorization: `Bearer ${token}` };
      if (contentType) headers['Content-Type'] = contentType;
      const response = await fetch(url, { method: 'POST', headers, body });
      if (response.ok) return response;

      // 4xx (except 429) won't get better by retrying.
      if (response.status < 500 && response.status !== 429) {
        throw new Error(`${url} responded ${response.status}: ${await safeText(response)}`);
      }
      lastError = new Error(`${url} responded ${response.status}`);
    } catch (error) {
      if (error instanceof Error && error.message.includes('responded 4')) throw error;
      lastError = error;
    }
    if (attempt < MAX_RETRIES) {
      const delaySeconds = attempt * 5;
      core.info(`Upload attempt ${attempt} failed, retrying in ${delaySeconds}s...`);
      await new Promise((resolve) => setTimeout(resolve, delaySeconds * 1000));
    }
  }
  throw lastError instanceof Error ? lastError : new Error(String(lastError));
}

async function safeText(response: Response): Promise<string> {
  try {
    return (await response.text()).slice(0, 500);
  } catch {
    return '';
  }
}

run();
