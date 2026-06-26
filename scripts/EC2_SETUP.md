# EC2 overnight dev — setup notes

Headless build/test box for MTile (.NET 8). Companion to `ec2-bootstrap.sh`.

## 1. Generate a deploy key (local, once)

```bash
ssh-keygen -t ed25519 -f ~/.ssh/mtile_deploy -N "" -C "mtile-ec2"
```

- `mtile_deploy.pub` → GitHub: repo **Settings → Deploy keys → Add** (read-only, no write).
- `mtile_deploy` (private) → base64 for user-data:

```bash
base64 -w0 ~/.ssh/mtile_deploy        # Git Bash;  PowerShell: [Convert]::ToBase64String([IO.File]::ReadAllBytes("$HOME\.ssh\mtile_deploy"))
```

## 2. Launch the instance

Prereq: AWS CLI configured (`aws configure`), an SSH keypair name for *your* login (`--key-name`), and a security group allowing inbound TCP 22 from your IP.

```bash
# user-data: pull the deploy key + run bootstrap on first boot
cat > /tmp/userdata.sh <<EOF
#!/usr/bin/env bash
export DEPLOY_KEY_B64="$(base64 -w0 ~/.ssh/mtile_deploy)"
export GIT_BRANCH="main"
curl -fsSL https://raw.githubusercontent.com/amdson/MTile/main/scripts/ec2-bootstrap.sh | bash
EOF

aws ec2 run-instances \
  --image-id ami-XXXXXXXX \            # Ubuntu 22.04/24.04 LTS for your region
  --instance-type t3.large \           # >=2 vCPU / 8GB for a comfy dotnet build
  --key-name YOUR_LOGIN_KEYPAIR \
  --security-group-ids sg-XXXXXXXX \
  --block-device-mappings '[{"DeviceName":"/dev/sda1","Ebs":{"VolumeSize":30}}]' \
  --user-data file:///tmp/userdata.sh \
  --tag-specifications 'ResourceType=instance,Tags=[{Key=Name,Value=mtile-dev}]'
```

Find an Ubuntu AMI for your region:
```bash
aws ec2 describe-images --owners 099720109477 \
  --filters "Name=name,Values=ubuntu/images/hvm-ssd/ubuntu-jammy-22.04-amd64-server-*" \
  --query 'sort_by(Images,&CreationDate)[-1].ImageId' --output text
```

## 3. Connect + watch bootstrap

```bash
aws ec2 describe-instances --filters "Name=tag:Name,Values=mtile-dev" \
  "Name=instance-state-name,Values=running" \
  --query 'Reservations[].Instances[].PublicDnsName' --output text   # get host

ssh -i ~/.ssh/YOUR_LOGIN_KEYPAIR.pem ubuntu@<host>
tail -f /var/log/cloud-init-output.log     # bootstrap progress (first boot)
```

When done, the repo is at `~/MTile`:
```bash
cd ~/MTile
dotnet build MTile.Core.csproj
dotnet test MTile.Tests/MTile.Tests.csproj
```

## 4. Re-run / re-sync by hand

```bash
bash ~/MTile/scripts/ec2-bootstrap.sh      # idempotent: ff-pulls + rebuilds
```

## 5. Stop / start / kill (save $)

```bash
ID=$(aws ec2 describe-instances --filters "Name=tag:Name,Values=mtile-dev" \
  --query 'Reservations[].Instances[].InstanceId' --output text)

aws ec2 stop-instances     --instance-ids $ID   # halt billing for compute (EBS still charged)
aws ec2 start-instances    --instance-ids $ID   # public DNS changes on restart
aws ec2 terminate-instances --instance-ids $ID  # destroy
```

Notes: user-data runs **only on first boot** — after a stop/start, re-sync via step 4. Stopped instances keep their EBS volume (small monthly charge); terminate to stop all billing.
