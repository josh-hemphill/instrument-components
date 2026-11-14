"""Retain machine stdout independently from diagnostics in qualification commands."""
import hashlib
import subprocess

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def execute_logged(name, command, *, cwd, env, output, structured_output=False):
    log = output / (name + '.log')
    diagnostics = output / (name + '.stderr.log')
    with log.open('w', encoding='utf-8') as stream:
        if structured_output:
            with diagnostics.open('w', encoding='utf-8') as errors:
                result = subprocess.run(list(map(str, command)), cwd=cwd, env=env, stdout=stream, stderr=errors)
        else:
            result = subprocess.run(list(map(str, command)), cwd=cwd, env=env, stdout=stream, stderr=subprocess.STDOUT)
    phase = dict(name=name, command=list(map(str, command)), exitCode=result.returncode, log=log.name, sha256=sha(log))
    if structured_output:
        phase.update(stderrLog=diagnostics.name, stderrSha256=sha(diagnostics))
    return phase

