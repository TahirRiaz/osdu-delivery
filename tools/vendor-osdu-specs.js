// Downloads the OSDU API contracts the delivery engine is built against into osdu/specs, pinned to the commit each
// was read at, and writes the provenance table. Run with node; needs network access to community.opengroup.org and
// github.com.
const fs = require('fs');
const path = require('path');

const root = path.join(__dirname, '..', 'osdu', 'specs');
const api = 'https://community.opengroup.org/api/v4/projects';

const sources = [
  { dir: 'wellbore-ddms', project: 98, repo: 'domain-data-mgmt-services/wellbore/wellbore-domain-services', files: [['docs/api/community/v1/openapi.json', 'openapi.json']] },
  { dir: 'seismic-ddms', project: 395, repo: 'domain-data-mgmt-services/seismic/seismic-dms-suite/seismic-store-service', files: [['app/sdms/docs/api/openapi.yaml', 'openapi.yaml']] },
  { dir: 'reservoir-ddms', project: 828, repo: 'domain-data-mgmt-services/reservoir/open-etp-server', files: [['src/lib/oes/common/etp12/etp-1.2.avpr', 'etp-1.2.avpr'], ['docs/bestPracticesForClients.md', 'best-practices-for-clients.md'], ['README.md', 'README.md']] },
  { dir: 'rafs-ddms', project: 1415, repo: 'domain-data-mgmt-services/rock-and-fluid-sample/rafs-ddms-services', files: [['docs/api/community/v2/openapi.yaml', 'openapi.yaml']] },
  { dir: 'well-delivery-ddms', project: 482, repo: 'domain-data-mgmt-services/well-delivery/well-delivery', files: [['docs/api/swagger.yaml', 'swagger.yaml']] },
  { dir: 'production-dspdm', project: 1245, repo: 'domain-data-mgmt-services/production/core/dspdm-services', files: [
    ['docs/Swagger-API.json', 'swagger-api.json'],
    ['src/dspdm.msp.mainservice/src/main/resources/openapi.yaml', 'mainservice.openapi.yaml'],
    ['src/business-api-spatialservice/src/main/resources/openapi.yaml', 'spatialservice.openapi.yaml'],
    ['src/business-api-volumeservice/src/main/resources/openapi.yaml', 'volumeservice.openapi.yaml'],
    ['src/business-api-wellflowmeasurement/src/main/resources/openapi.yaml', 'wellflowmeasurement.openapi.yaml'],
    ['src/business-api-wellstatus/src/main/resources/openapi.yaml', 'wellstatus.openapi.yaml'],
  ] },
  { dir: 'production-timeseries', project: 783, repo: 'domain-data-mgmt-services/production/historian/services/pddms-timeseries-ingestion', files: [['docs/api/azure/openapi.yaml', 'ingestion.openapi.yaml']] },
  { dir: 'production-timeseries', project: 790, repo: 'domain-data-mgmt-services/production/historian/services/pddms-timeseries', files: [['docs/api/azure/openapi.yaml', 'timeseries.openapi.yaml']] },
  { dir: 'reservoir-management-ddms', project: 1470, repo: 'domain-data-mgmt-services/reservoir-management/reservoir-management-domain-services', files: [['docs/Reservoir Management Domain Data Management Service.postman_collection.json', 'postman-collection.json'], ['README.md', 'README.md']] },
  { dir: 'eds-dms', project: 1247, repo: 'data-flow/ingestion/external-data-sources/eds-dms', files: [['docs/api/community/v1/openapi.yaml', 'openapi.yaml']] },
  { dir: 'workflows', project: 147, repo: 'data-flow/ingestion/ingestion-dags', files: [['README.md', 'ingestion-dags.md']] },
  { dir: 'workflows', project: 668, repo: 'data-flow/ingestion/osdu-airflow-lib', files: [['README.md', 'osdu-airflow-lib.md']] },
  { dir: 'workflows', project: 202, repo: 'data-flow/ingestion/csv-parser/csv-parser', files: [['README.md', 'csv-parser.md']] },
  { dir: 'workflows', project: 1414, repo: 'data-flow/ingestion/energistics/energistics-parser-dag', files: [['README.md', 'energistics-parser-dag.md']] },
  { dir: 'workflows', project: 469, repo: 'data-flow/ingestion/segy-to-vds-conversion', files: [['README.md', 'segy-to-vds-conversion.md']] },
  { dir: 'workflows', project: 460, repo: 'data-flow/ingestion/segy-to-zgy-conversion', files: [['README.md', 'segy-to-zgy-conversion.md']] },
  { dir: 'workflows', project: 1551, repo: 'data-flow/ingestion/segy-to-mdio-conversion-dag', files: [['README.md', 'segy-to-mdio-conversion-dag.md']] },
  { dir: 'workflows', project: 407, repo: 'data-flow/ingestion/external-data-sources/core-external-data-workflow', files: [['README.md', 'core-external-data-workflow.md']] },
];

// Releases of projects outside OSDU whose REST contracts a route calls: the Airflow instance behind the Workflow service,
// for the run outputs only its API returns (osdu/specs/workflows/INTEGRATION.md section 5.2). They are pinned to the
// releases OSDU deployments run (Airflow 2.11 serves /api/v1, Airflow 3 /api/v2), not to a branch head, so the script
// fetches the tag each row names and moves a row only when its release is changed here.
const releases = [
  { dir: 'workflows/airflow', owner: 'apache', name: 'airflow', tag: '2.11.2', files: [['airflow/api_connexion/openapi/v1.yaml', 'v1.yaml']] },
  { dir: 'workflows/airflow', owner: 'apache', name: 'airflow', tag: '3.3.1', files: [
    ['airflow-core/src/airflow/api_fastapi/core_api/openapi/v2-rest-api-generated.yaml', 'v2-rest-api-generated.yaml'],
    ['airflow-core/src/airflow/api_fastapi/auth/managers/simple/openapi/v2-simple-auth-manager-generated.yaml', 'v2-simple-auth-manager-generated.yaml'],
  ] },
];

const TextFile = /\.(md|json|ya?ml|avpr)$/i;
const EmDash = String.fromCharCode(0x2014);
// A generated contract can write the em dash as a JSON escape, which decodes to the character.
const EmDashEscape = String.fromCharCode(0x5c) + 'u2014';

function asRepositoryText(name, bytes) {
  if (!TextFile.test(name)) {
    return bytes;
  }

  const text = bytes.toString('utf8');
  if (!text.includes(EmDash) && !text.includes(EmDashEscape)) {
    return bytes;
  }

  // A spaced hyphen keeps a YAML plain scalar a scalar, where a colon would start a mapping.
  let plain = text;
  for (const dash of [EmDash, EmDashEscape]) {
    plain = plain.split(' ' + dash + ' ').join(' - ').split(dash).join('-');
  }

  return Buffer.from(plain, 'utf8');
}

async function github(url) {
  const response = await fetch(url, { headers: { 'User-Agent': 'osdu-delivery-vendor-specs' } });
  if (!response.ok) throw new Error(`${url}: ${response.status}`);
  return response;
}

/** The commit a release tag names, through an annotated tag object when the tag is one. */
async function releaseCommit(release) {
  const repo = `https://api.github.com/repos/${release.owner}/${release.name}`;
  let target = (await (await github(`${repo}/git/refs/tags/${encodeURIComponent(release.tag)}`)).json()).object;
  while (target.type === 'tag') {
    target = (await (await github(`${repo}/git/tags/${target.sha}`)).json()).object;
  }

  const commit = await (await github(`${repo}/commits/${target.sha}`)).json();
  return { sha: target.sha, date: commit.commit.committer.date.slice(0, 10) };
}

async function json(url) {
  const response = await fetch(url);
  if (!response.ok) throw new Error(`${url}: ${response.status}`);
  return response.json();
}

async function main() {
  const rows = [];
  for (const source of sources) {
    const project = await json(`${api}/${source.project}`);
    const branch = project.default_branch;
    const head = await json(`${api}/${source.project}/repository/branches/${encodeURIComponent(branch)}`);
    const commit = head.commit.id;
    fs.mkdirSync(path.join(root, source.dir), { recursive: true });
    for (const [from, to] of source.files) {
      const url = `${api}/${source.project}/repository/files/${encodeURIComponent(from)}/raw?ref=${commit}`;
      const response = await fetch(url);
      if (!response.ok) throw new Error(`${url}: ${response.status}`);
      // The repository allows no em dash anywhere, so one in a text contract becomes plain punctuation.
      const bytes = asRepositoryText(to, Buffer.from(await response.arrayBuffer()));
      fs.writeFileSync(path.join(root, source.dir, to), bytes);
      rows.push({ file: `${source.dir}/${to}`, repo: source.repo, from, commit, date: head.commit.committed_date.slice(0, 10), size: bytes.length });
      console.log(`${source.dir}/${to}  ${bytes.length} bytes  ${commit.slice(0, 12)}`);
    }
  }

  for (const release of releases) {
    const commit = await releaseCommit(release);
    fs.mkdirSync(path.join(root, release.dir), { recursive: true });
    for (const [from, to] of release.files) {
      const response = await github(`https://raw.githubusercontent.com/${release.owner}/${release.name}/${commit.sha}/${from}`);
      const bytes = asRepositoryText(to, Buffer.from(await response.arrayBuffer()));
      fs.writeFileSync(path.join(root, release.dir, to), bytes);
      rows.push({ file: `${release.dir}/${to}`, repo: `github.com/${release.owner}/${release.name} (release ${release.tag})`, from, commit: commit.sha, date: commit.date, size: bytes.length });
      console.log(`${release.dir}/${to}  ${bytes.length} bytes  ${commit.sha.slice(0, 12)} (${release.tag})`);
    }
  }

  // The core specifications are copied from the local specification set, and a generated contract is written by its
  // generator (generatedBy), so neither is downloaded here and their rows are kept. A generated contract whose project
  // has moved on is named, so its generator is run again against the new commit.
  const listed = path.join(root, 'sources.json');
  const kept = fs.existsSync(listed)
    ? JSON.parse(fs.readFileSync(listed, 'utf8')).filter((row) => row.file.startsWith('core/') || row.generatedBy)
    : [];
  for (const row of kept.filter((r) => r.generatedBy)) {
    const current = rows.find((r) => r.repo === row.repo);
    if (current && current.commit !== row.commit) {
      console.warn(`${row.file} was generated at ${row.commit.slice(0, 12)} and ${row.repo} is now at ${current.commit.slice(0, 12)}: run ${row.generatedBy} against the new commit.`);
    }
  }

  fs.writeFileSync(listed, JSON.stringify([...rows, ...kept], null, 2) + '\n');
}

main().catch((error) => { console.error(error); process.exit(1); });
