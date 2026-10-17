## Create a secret

Use the command
```
kubectl create secret docker-registry regcred --docker-server=registry.neuhold-leonhard.work --docker-username=leonhard --docker-password=<password>
```
to create the secret for logging into the docker registry.

Then use it as follows in the kubernetes .yaml deployment definition:
```asdf

```asdf
