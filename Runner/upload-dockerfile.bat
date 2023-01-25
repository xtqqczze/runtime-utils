call az acr login --name runtimeutils
call docker build --progress=plain -t runner:latest .
call docker tag runner:latest runtimeutils.azurecr.io/runner
call docker push runtimeutils.azurecr.io/runner