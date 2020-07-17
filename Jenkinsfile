pipeline {
    triggers {
        cron('0 0 1 * *')
    }
    agent {
        node {
            label 'master'
        }
    }
    environment {
        BlobStorageConnectionString = credentials('JenkinsAzureBackupBlobStorageConnectionString')
    }
    stages {
        stage ('Backup') {
            steps {
                powershell './build.ps1 BackupInstance'
            }

        }
    }
    post {
        always {
            step([$class: 'Mailer',
                notifyEveryUnstableBuild: true,
                recipients: "georg@dangl.me",
                sendToIndividuals: true])
            cleanWs()
        }
    }
}
