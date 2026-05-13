pipeline {
    agent none

    environment {
        SOLUTION = 'Oragon.ElasticPool.slnx'
        LIVE_DASHBOARD_SOLUTION = 'samples/Oragon.ElasticPool.RabbitMQ.Sample.LiveDashboard/Oragon.ElasticPool.RabbitMQ.Sample.LiveDashboard.slnx'
        CONFIGURATION = 'Release'
        COVERAGE_FILE = '/output-coverage/coverage.xml'
    }

    stages {
        stage('Build') {
            matrix {
                axes {
                    axis {
                        name 'TFM'
                        values 'net8.0', 'net9.0', 'net10.0'
                    }
                }
                agent {
                    dockerfile {
                        args '-u root:root'
                        //args '-u root:root -v /gago/nuget-cache:/root/.nuget/packages'
                    }
                }
                stages {
                    stage('Restore') {
                        steps {
                            sh 'dotnet restore "${SOLUTION}"'
                            sh 'dotnet restore "${LIVE_DASHBOARD_SOLUTION}"'
                        }
                    }
                    stage('Compile') {
                        steps {
                            sh 'dotnet build "${SOLUTION}" -c "${CONFIGURATION}" --no-restore -f "${TFM}"'
                            sh 'dotnet build "${LIVE_DASHBOARD_SOLUTION}" -c "${CONFIGURATION}" --no-restore -f "${TFM}"'
                        }
                    }
                }
            }
        }

        stage('Test') {
            matrix {
                axes {
                    axis {
                        name 'TFM'
                        values 'net8.0', 'net9.0', 'net10.0'
                    }
                }
                agent {
                    dockerfile {
                        args '-u root:root -v /var/run/docker.sock:/var/run/docker.sock '
                        // args '-u root:root -v /var/run/docker.sock:/var/run/docker.sock -v /gago/nuget-cache:/root/.nuget/packages'
                    }
                }
                stages {
                    stage('Run Tests') {
                        steps {
                            sh 'dotnet restore "${SOLUTION}"'
                            sh 'dotnet build "${SOLUTION}" -c "${CONFIGURATION}" --no-restore -f "${TFM}"'
                            sh 'dotnet test --solution "${SOLUTION}" -c "${CONFIGURATION}" --no-build -f "${TFM}"'
                        }
                    }
                }
            }
        }

        stage('SonarCloud') {
            agent {
                dockerfile {
                    args '-u root:root -v /var/run/docker.sock:/var/run/docker.sock '
                    //args '-u root:root -v /var/run/docker.sock:/var/run/docker.sock -v /gago/nuget-cache:/root/.nuget/packages'
                }
            }
            steps {
                withCredentials([usernamePassword(credentialsId: 'SonarQube', passwordVariable: 'SONARQUBE_KEY', usernameVariable: 'DUMMY')]) {
                    script {
                        def sonarParams = [
                            '/k:"Oragon.ElasticPool"',
                            '/o:luizcarlosfaria',
                            '/d:sonar.token="$SONARQUBE_KEY"',
                            '/d:sonar.host.url="https://sonarcloud.io"',
                            "/d:sonar.cs.vscoveragexml.reportsPaths=${env.COVERAGE_FILE}"
                        ]

                        if (env.BRANCH_NAME != null && env.BRANCH_NAME != 'main' && !env.BRANCH_NAME.startsWith('PR-') && !env.BRANCH_NAME.startsWith('v')) {
                            sonarParams << '/d:sonar.branch.name="$BRANCH_NAME"'
                            sonarParams << '/d:sonar.branch.target=main'
                        }

                        def sonarParamsText = sonarParams.join(' ')

                        sh """
                            #set -euo pipefail
                            mkdir -p /output-coverage
                            git fetch origin main:main || true
                            dotnet restore "${env.SOLUTION}"
                            dotnet sonarscanner begin ${sonarParamsText}
                            dotnet build "${env.SOLUTION}" -c "${env.CONFIGURATION}" --no-incremental -f net10.0
                            dotnet-coverage collect "dotnet test --solution ${env.SOLUTION} -c ${env.CONFIGURATION} --no-build -f net10.0" -f xml -o "${env.COVERAGE_FILE}"
                            dotnet sonarscanner end /d:sonar.token="\\$SONARQUBE_KEY"
                        """
                    }
                }
            }
        }

        stage('Pack') {
            agent {
                dockerfile {
                     args '-u root:root'
                    // args '-u root:root -v /gago/nuget-cache:/root/.nuget/packages'
                }
            }
            when { buildingTag() }
            steps {
                script {
                    def projects = [
                        'Oragon.ElasticPool.Core',
                        'Oragon.ElasticPool.RabbitMQ'
                    ]
                    def packageVersion = env.BRANCH_NAME.startsWith('v') ? env.BRANCH_NAME.substring(1) : env.BRANCH_NAME

                    sh 'rm -rf ./output-packages && mkdir -p ./output-packages'

                    for (int i = 0; i < projects.size(); ++i) {
                        sh "dotnet pack ./src/${projects[i]}/${projects[i]}.csproj -c Release -p:PackageVersion=${packageVersion} -p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg --output ./output-packages"
                    }
                }
            }
        }

        stage('Publish') {
            agent {
                dockerfile {
                    args '-u root:root'
                    //args '-u root:root -v /gago/nuget-cache:/root/.nuget/packages'
                }
            }
            when { buildingTag() }
            steps {
                script {
                    def publishOnNuGet = !env.BRANCH_NAME.endsWith('-alpha')

                    withCredentials([usernamePassword(credentialsId: 'myget-oragon', passwordVariable: 'MYGET_KEY', usernameVariable: 'DUMMY')]) {
                        sh 'for pkg in ./output-packages/*.nupkg ; do dotnet nuget push "$pkg" -k "$MYGET_KEY" -s https://www.myget.org/F/oragon/api/v2/package ; done'
                        sh 'for pkg in ./output-packages/*.snupkg ; do dotnet nuget push "$pkg" -k "$MYGET_KEY" -s https://www.myget.org/F/oragon/api/v3/index.json ; done'
                    }

                    if (publishOnNuGet) {
                        withCredentials([usernamePassword(credentialsId: 'nuget-luizcarlosfaria', passwordVariable: 'NUGET_KEY', usernameVariable: 'DUMMY')]) {
                            sh 'for pkg in ./output-packages/*.nupkg ; do dotnet nuget push "$pkg" -k "$NUGET_KEY" -s https://api.nuget.org/v3/index.json ; done'
                        }
                    }
                }
            }
        }
    }
}
