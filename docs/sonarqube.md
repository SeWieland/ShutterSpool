# SonarQube Cloud analysis

The [SonarQube workflow](../.github/workflows/sonarqube.yml) analyzes the .NET solution and Vue/TypeScript frontend in one public SonarQube Cloud project.
It runs on pushes to `main`, same-repository pull requests targeting `main`, and manual dispatches after the repository variables below are configured.
Generated API clients, OpenAPI documents, dependencies and build output are excluded.

Analysis has its own build because SonarScanner changes compiler warning handling.
The existing CI, dependency review and CodeQL checks remain independent.
This workflow uploads static-analysis results; it does not run tests or import coverage reports.
Continue to use the [test guide](../tests/README.md) for behavior and recovery verification.

## Set up the service

1. Sign in to [SonarQube Cloud](https://sonarcloud.io) with GitHub and create or import an organization in the **EU region**.
   Choose **Get SonarQube for OSS** during organization creation; the OSS plan supports public projects, branch analysis and pull-request analysis.
   See [Sonar's signup instructions](https://docs.sonarsource.com/sonarqube-cloud/administering-sonarcloud/managing-subscription/signing-up-for-plan#oss).
2. Authorize the SonarQube Cloud GitHub App for the public ShutterSpool repository and import that repository as a public project.
   Keep the project's main branch named `main`.
3. In the project's analysis settings, choose **GitHub Actions / .NET** and disable **Automatic Analysis** if enabled.
   This workflow supplies both backend and frontend analysis; do not run a second automatic scan for the same project.
4. In **My account → Access Tokens → Personal Tokens**, [generate a personal access token](https://docs.sonarsource.com/sonarqube-cloud/managing-your-account/managing-tokens) using a Sonar account with **Execute Analysis** permission on the project.
   Store it directly in GitHub; do not put it in a file, issue, pull request or chat.
   Before it expires, generate a replacement, update the GitHub `SONAR_TOKEN` secret, and revoke the old token.

The workflow targets the EU service at `https://sonarcloud.io`.

## Configure GitHub

In the repository, open **Settings → Secrets and variables → Actions** and add:

| Kind | Name | Value |
| --- | --- | --- |
| Repository secret | `SONAR_TOKEN` | The analysis token. |
| Repository variable | `SONAR_ORGANIZATION` | The organization key from SonarQube Cloud, not its display name. |
| Repository variable | `SONAR_PROJECT_KEY` | The project key from SonarQube Cloud, not the repository URL. |

Set the secret first, then both variables.
Until both variables exist, the analysis job is skipped.
Once they exist, a missing token fails the job with a setup error; scanner, authentication and build failures also fail the job.

After merging the workflow, use **Actions → SonarQube → Run workflow → main** for the initial scan, or inspect the run triggered by the merge.
Confirm the dashboard shows both C# and frontend source files, excludes the generated client, and has a completed analysis for that commit.
Then open a same-repository pull request to confirm the SonarQube Cloud GitHub App reports its quality gate.

## Quality gate and contributor limits

Start with the scan and quality gate as advisory checks while reviewing the initial findings.
The workflow does not wait for or fail on a quality-gate result; a successful upload is not a passing quality gate.
SonarQube Cloud computes and reports the gate separately through its GitHub App.
Configure the new-code definition and review the gate before making a merge rule depend on it.
Coverage is not collected by this integration, so coverage conditions are not evidence of test coverage and should not be made merge-blocking until report import is configured.

Fork and Dependabot pull requests skip the scan because they do not receive the Actions secret.
Their changes are scanned after merging to `main`; existing CI and CodeQL still follow their own policies.
Do not make the SonarQube Cloud App check universally required while these pull requests cannot produce it.
Do not use `pull_request_target` to build contributor code with the analysis token, or copy the token into forks or Dependabot secrets.

The scanner version is pinned in the workflow; update it deliberately alongside the [scanner release notes](https://docs.sonarsource.com/sonarqube-cloud/analyzing-source-code/scanners/sonarscanner-for-dotnet/installing).
No project credentials or service configuration are required for ordinary local builds or tests.
