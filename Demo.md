ASP.NET Core and Azure SQL Database app with OSM on AKS
=======================================================

Based on [Tutorial: Build an ASP.NET Core and Azure SQL Database app in Azure App Service](https://docs.microsoft.com/en-us/azure/app-service/tutorial-dotnetcore-sqldb-app?pivots=platform-linux).

Prequisites
-----------

* Common variables:

```sh
LOCATION="australiaeast"
RG_NAME="osm-demo"
CLUSTER="osm-demo"
NODE_COUNT=2

az group create --name $RG_NAME --location $LOCATION
```

* Basic AKS cluster and kubeconfig accessible locally

```sh
az provider register --namespace Microsoft.OperationsManagement
az provider register --namespace Microsoft.OperationalInsights

az provider show -n Microsoft.OperationsManagement -o table
az provider show -n Microsoft.OperationalInsights -o table

az aks create --resource-group $RG_NAME --name $CLUSTER --node-count $NODE_COUNT --enable-addons monitoring --generate-ssh-keys

az aks get-credentials --resource-group $RG_NAME --name $CLUSTER
az aks install-cli

kubectl get node -o wide
```

* OpenSSL installed locally

```sh
sudo apt-get update -y
sudo apt-get install -y openssl
```

* Helm 3

Steps
-----

Create the SQL DB:

```sh
SQL_SERVER_NAME="todoserver-$RANDOM"
ADMIN_USERNAME="todoadmin"

openssl rand -base64 14 > password.txt
ADMIN_PASSWORD=$(<password.txt)

az sql server create \
    --name $SQL_SERVER_NAME \
    --resource-group $RG_NAME \
    --location $LOCATION \
    --admin-user $ADMIN_USERNAME \
    --admin-password $ADMIN_PASSWORD

az sql server firewall-rule create \
    --resource-group $RG_NAME \
    --server $SQL_SERVER_NAME \
    --name AllowAzureIps \
    --start-ip-address 0.0.0.0 \
    --end-ip-address 0.0.0.0

# Optional for local access to the DB
MY_IP="$(curl -s https://ifconfig.me)"

# Optional for local access to the DB
az sql server firewall-rule create \
    --name AllowLocalClient \
    --server $SQL_SERVER_NAME \
    --resource-group $RG_NAME \
    --start-ip-address=$MY_IP \
    --end-ip-address=$MY_IP

az sql db create \
    --resource-group $RG_NAME \
    --server $SQL_SERVER_NAME \
    --name coreDB \
    --service-objective S0

DB_CONN_STRING=$(az sql db show-connection-string \
    --client ado.net --server $SQL_SERVER_NAME \
    --name coreDB | sed -e "s/<username>/$ADMIN_USERNAME/" -e "s/<password>/$ADMIN_PASSWORD/")

export DB_CONN_STRING
cat appsettings.Production.json.template | envsubst > appsettings.Production.json

# Target SQL Server (instead of SQLite)
rm -rf Migrations
ASPNETCORE_ENVIRONMENT=Production dotnet ef migrations add InitialCreate
ASPNETCORE_ENVIRONMENT=Production dotnet ef database update
```

Create Azure Container Registry
-------------------------------

```sh
ACR_NAME="todoapp$RANDOM"

az acr create --resource-group $RG_NAME \
  --name $ACR_NAME --sku Basic --admin-enabled
```

Build and deploy the application
--------------------------------

Build image in ACR:

```sh
az acr build --image todoapp:v4 \
  --registry $ACR_NAME \
  --file Dockerfile .
```

Deploy ingress controller:

```sh
helm repo add ingress-nginx https://kubernetes.github.io/ingress-nginx
helm repo update
kubectl create namespace app-ingress

helm install app-ingress ingress-nginx/ingress-nginx \
    --namespace app-ingress \
    --set controller.replicaCount=2 \
    --set controller.nodeSelector."kubernetes\.io/os"=linux \
    --set controller.admissionWebhooks.patch.nodeSelector."kubernetes\.io/os"=linux \
    --set defaultBackend.nodeSelector."kubernetes\.io/os"=linux \
    --set controller.ingressClass=app-public \
    --set controller.ingressClassResource.name=app-public \
    --set controller.ingressClassResource.controllerValue="k8s.io/ingress-nginx"

kubectl wait --namespace app-ingress \
  --for=condition=ready pod \
  --selector=app.kubernetes.io/component=controller \
  --timeout=120s
```

Create Image Pull Secret:

```sh
ACR_USER=$(az acr credential show -n $ACR_NAME --query username -o tsv)
ACR_PASSWORD=$(az acr credential show -n $ACR_NAME --query passwords[0].value -o tsv)

kubectl create secret docker-registry todo-registry --docker-server=$ACR_NAME.azurecr.io --docker-username=$ACR_USER --docker-password=$ACR_PASSWORD --docker-email=admin@example.com -n todoapp
```

Deploy app:

```sh
kubectl create ns todoapp
export ACR_NAME

kubectl create secret generic appsettings --from-file=appsettings.Production.json -n todoapp

cat Kubernetes/todoapp.deploy.yaml | envsubst | kubectl apply -f - -n todoapp
kubectl apply -f Kubernetes/todoapp.svc.yaml -n todoapp
kubectl apply -f Kubernetes/todoapp.ingress.yaml -n todoapp
```

Access the Todo App and verify it is working and stores todos in Azure SQL DB:

```sh
kubectl get ingress -n todoapp
# <ingress_ip>
# Browse to: http://<ingress_ip>
```

Install OSM via AKS add-on
--------------------------

```sh
az extension add --name aks-preview
# or
az extension update --name aks-preview

az feature register --namespace "Microsoft.ContainerService" --name "AKS-OpenServiceMesh"
az feature list -o table --query "[?contains(name, 'Microsoft.ContainerService/AKS-OpenServiceMesh')].{Name:name,State:properties.state}"
az provider register --namespace Microsoft.ContainerService

az aks enable-addons --addons open-service-mesh -g $RG_NAME -n $CLUSTER
az aks list -g $RG_NAME -o json | jq -r '.[].addonProfiles.openServiceMesh.enabled'

kubectl get deployments -n kube-system --selector app=osm-controller
kubectl get pods -n kube-system --selector app=osm-controller
kubectl get services -n kube-system --selector app=osm-controller
```

Configure OSM
-------------

```sh
kubectl get meshconfig osm-mesh-config -n kube-system -o yaml
kubectl patch meshconfig osm-mesh-config -n kube-system -p '{"spec":{"traffic":{"enablePermissiveTrafficPolicyMode":false}}}' --type=merge
kubectl patch meshconfig osm-mesh-config -n kube-system -p '{"spec":{"traffic":{"enableEgress":false}}}' --type=merge
kubectl patch meshconfig osm-mesh-config -n kube-system -p '{"spec":{"featureFlags":{"enableEgressPolicy":true}}}' --type=merge
```

Install OSM client library
--------------------------

```sh
OSM_VERSION=v0.9.1
curl -sL "https://github.com/openservicemesh/osm/releases/download/$OSM_VERSION/osm-$OSM_VERSION-linux-amd64.tar.gz" | tar -vxzf -
sudo mv ./linux-amd64/osm /usr/local/bin/osm
sudo chmod +x /usr/local/bin/osm
osm version
```

Onboard todoapp to OSM
----------------------

```sh
osm namespace add todoapp
osm namespace list
kubectl get pod -n todoapp

kubectl delete deploy todoapp -n todoapp
cat Kubernetes/todoapp.deploy.yaml | envsubst | kubectl apply -f - -n todoapp
kubectl get pod -n todoapp
```

The todoapp should not work as it needs egress to Azure SQL DB.

Error displayed in browser: "upstream request timeout"

We can check the pod logs:

```sh
# Todoapp
kubectl logs pod/todoapp-d87454c7f-cd6sw -n todoapp todoapp -f
# --> An error occurred using the connection to database 'coreDB' on server 'tcp:todoserver-xxxxx.database.windows.net,1433'.

# OSM sidecar
kubectl logs pod/todoapp-d87454c7f-cd6sw -n todoapp envoy -f
# --> "upstream_response_timeout"}
```

Enable egress
-------------

Enable egress traffic to allow access to Azure SQL DB:

```sh
kubectl patch meshconfig osm-mesh-config -n kube-system -p '{"spec":{"traffic":{"enableEgress":true}}}' --type=merge
kubectl delete deploy todoapp -n todoapp
cat Kubernetes/todoapp.deploy.yaml | envsubst | kubectl apply -f - -n todoapp
kubectl get pod -n todoapp
```

OSM troubleshooting
-------------------

```sh
# OSM controller logs
kubectl logs -n kube-system $(kubectl get pod -n kube-system -l app=osm-controller -o jsonpath='{.items[0].metadata.name}') | grep error
```

Cleanup
-------

```sh
az group delete -n $RG_NAME
```

TODO
----

* Add fine grained egress access policies

Resources
---------

* [Tutorial: Build an ASP.NET Core and Azure SQL Database app in Azure App Service](https://docs.microsoft.com/en-us/azure/app-service/tutorial-dotnetcore-sqldb-app?pivots=platform-linux)
* [Deploy the Open Service Mesh AKS add-on using Azure CLI](https://docs.microsoft.com/en-us/azure/aks/open-service-mesh-deploy-addon-az-cli)
