"""Called only after required CI jobs succeed. Never overwrites existing tags/assets."""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET


def main():
    version = json.loads(Path('VERSION.json').read_text(encoding='utf-8'))
    tag = version['release_tag']
    if not re.fullmatch(r'v\d+\.\d+\.\d+-alpha\.\d+', tag):
        raise RuntimeError('This workflow publishes alpha previews only')
    if tag != 'v' + version['version']:
        raise RuntimeError('Version/tag mismatch')
    assembly = ET.parse('Directory.Build.props').findtext('.//Version')
    if assembly != version['version']:
        raise RuntimeError('Application/version manifest mismatch')
    repo, sha = os.environ['GITHUB_REPOSITORY'], os.environ['GITHUB_SHA']
    token = os.environ['GH_TOKEN']
    request = urllib.request.Request(
        f'https://api.github.com/repos/{repo}/git/ref/tags/{tag}',
        headers={'Authorization': f'Bearer {token}', 'Accept': 'application/vnd.github+json'})
    try:
        with urllib.request.urlopen(request) as response:
            json.load(response)
        print(f'{tag} already exists; immutable release left unchanged. Bump VERSION.json for a new build.')
        return
    except urllib.error.HTTPError as error:
        if error.code != 404:
            raise
    artifacts = Path('artifacts')
    required = ['LanBridge-Windows-x64.zip', 'linux-core-tests.txt',
                'windows-core-tests.txt', 'windows-drag-self-test.txt', 'windows-publish.txt']
    for name in required:
        if not (artifacts / name).is_file() or (artifacts / name).stat().st_size == 0:
            raise RuntimeError(f'Missing artifact: {name}')
    subprocess.run(['git', 'archive', '--format=zip', '--prefix=LanBridge/',
                    '-o', str(artifacts / 'LanBridge-Source.zip'), sha], check=True)
    assets = {p.name: {'sha256': hashlib.sha256(p.read_bytes()).hexdigest(),
                       'bytes': p.stat().st_size} for p in sorted(artifacts.iterdir()) if p.is_file()}
    manifest = dict(version, source_commit=sha,
                    workflow_run_url=f'https://github.com/{repo}/actions/runs/{os.environ["GITHUB_RUN_ID"]}',
                    workflow_run_attempt=os.environ.get('GITHUB_RUN_ATTEMPT', '1'),
                    automated_checks={'linux_core': 'passed', 'windows_core': 'passed',
                                      'windows_com_drag_data': 'passed', 'windows_publish': 'passed'},
                    windows_ci_note='Windows Server runner, not physical Windows 11 dual-PC acceptance',
                    assets=assets)
    (artifacts / 'release-manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    checksums = ''.join(f'{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.name}\n'
                        for p in sorted(artifacts.iterdir()) if p.is_file() and p.name != 'SHA256SUMS.txt')
    (artifacts / 'SHA256SUMS.txt').write_text(checksums, encoding='utf-8')
    subprocess.run(['gh', 'release', 'create', tag, '--repo', repo, '--target', sha,
                    '--draft', '--prerelease', '--title', f'LanBridge {tag}',
                    '--notes-file', f'docs/releases/{tag}.md'], check=True)
    subprocess.run(['gh', 'release', 'upload', tag, '--repo', repo,
                    *[str(p) for p in sorted(artifacts.iterdir()) if p.is_file()]], check=True)
    subprocess.run(['gh', 'release', 'edit', tag, '--repo', repo, '--draft=false', '--prerelease'], check=True)


if __name__ == '__main__':
    main()
