az acr login --name runtimeutils
docker build --progress=plain -t runner:latest .
docker tag runner:latest runtimeutils.azurecr.io/runner
docker push runtimeutils.azurecr.io/runner