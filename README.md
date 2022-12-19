https://docs.microsoft.com/dotnet/devops/create-dotnet-github-action  
https://github.com/dotnet/samples/tree/main/github-actions/DotNet.GitHubAction
```yaml
name: '.NET code metrics'

on:
  push:
    branches: [ master ]
    paths-ignore:
    - '**.md'
jobs:
  build:
    runs-on: ubuntu-latest
    permissions:
        contents: write
        pull-requests: write

    steps:
    - uses: actions/checkout@v2
    - name: .NET code metrics
      id: dotnet-code-metrics
      uses: Silverdimond/SilverCommandRefGen@master
      with:
        owner: ${{ github.repository_owner }}
        name: ${{ github.repository }}
        branch: ${{ github.ref }}
        dir: ${{ './' }}

    - name: Create pull request
      uses: peter-evans/create-pull-request@v3.4.1
      if: ${{ steps.dotnet-code-metrics.outputs.updated-metrics }} == 'true'
      with:
        title: '${{ steps.dotnet-code-metrics.outputs.summary-title }}'
        body: '${{ steps.dotnet-code-metrics.outputs.summary-details }}'
        commit-message: "SilverCommandRefGen automated pull request.'
```