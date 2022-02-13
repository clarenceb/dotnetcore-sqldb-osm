Add OSM to an existing app running on AKS
=========================================

This is an example application based on the [Tutorial: Build an ASP.NET Core and Azure SQL Database app in Azure App Service](https://docs.microsoft.com/en-us/azure/app-service/tutorial-dotnetcore-sqldb-app?pivots=platform-linux) which has been modified for the purposes of demonstrating some capabilities of Open Service Mesh (OSM).

* Tested with: OSM v1.0.0 (self-installed, OSS version)
* Local environment used: WSL2/Ubuntu
* Kubernetes environment: AKS v1.21.7

**Note:** OSM works with any Kubernetes cluster and is not dependent on Azure in any way.

Prequisites
-----------

* Common variables (adjust as appropriate):

```sh
LOCATION="australiaeast"
RG_NAME="aks-demos"
CLUSTER="aks-demos"
NODE_COUNT=3
```

* Create the Azure Resource Group to contain all Azure Resources for the demo:

```sh
az group create --name $RG_NAME --location $LOCATION
```

* Create a basic AKS cluster with the OSM add-on enabled:

```sh
az provider register --namespace Microsoft.OperationsManagement
az provider register --namespace Microsoft.OperationalInsights

az provider show -n Microsoft.OperationsManagement -o table
az provider show -n Microsoft.OperationalInsights -o table

az aks create \
  --resource-group $RG_NAME \
  --name $CLUSTER \
  --node-count $NODE_COUNT \
  --enable-addons monitoring --generate-ssh-keys

az aks get-credentials \
--resource-group $RG_NAME \
--name $CLUSTER \
--overwrite-existing

az aks install-cli

kubectl get node -o wide
```

* Install Helm 3:

Follow the [official installation instructions](https://helm.sh/docs/intro/install/)

* Install OSM:

Follow steps here for various platforms: https://release-v1-0.docs.openservicemesh.io/docs/getting_started/setup_osm/

Summary steps:

```sh
# OSM CLI installation
system=$(uname -s)
release=v1.0.0
curl -L https://github.com/openservicemesh/osm/releases/download/${release}/osm-${release}-${system}-amd64.tar.gz | tar -vxzf -
sudo mv ./${system}-amd64/osm /usr/local/bin/osm
osm version

export osm_namespace=osm-system
export osm_mesh_name=osm

# OSM installation to Kubernetes, including components: Prometheus, Grafana, Jaeger, Contour (Ingress)
osm install \
    --mesh-name "$osm_mesh_name" \
    --osm-namespace "$osm_namespace" \
    --set=osm.enablePermissiveTrafficPolicy=true \
    --set=osm.deployPrometheus=true \
    --set=osm.deployGrafana=true \
    --set=osm.deployJaeger=true \
    --set contour.enabled=true \
    --set contour.configInline.tls.envoy-client-certificate.name=osm-contour-envoy-client-cert \
    --set contour.configInline.tls.envoy-client-certificate.namespace="$osm_namespace"
```

* Verify OSM is installed:

```sh
kubectl get pod -n osm-system
```

All pods should be "Running":

```sh
NAME                                   READY   STATUS    RESTARTS   AGE
jaeger-7695dbf8b5-ff8vv                1/1     Running   0          71s
osm-bootstrap-6df985c6-vrf2n           1/1     Running   0          71s
osm-contour-contour-6c98586744-fmzxc   1/1     Running   0          71s
osm-contour-contour-6c98586744-h9jvq   1/1     Running   0          71s
osm-contour-envoy-9pvqq                2/2     Running   0          71s
osm-contour-envoy-jzxdw                2/2     Running   0          71s
osm-contour-envoy-x268j                2/2     Running   0          71s
osm-controller-7b5b547d4b-86hs9        1/1     Running   0          71s
osm-grafana-6c4fc6644c-fz2cw           1/1     Running   0          71s
osm-injector-5b7cf8f99f-s4c6r          1/1     Running   0          71s
osm-prometheus-57b87cb4dc-tjfph        1/1     Running   0          71s
```

* Optional: ensure OpenSSL is installed locally (for random password generation):

```sh
sudo apt-get update -y
sudo apt-get install -y openssl
```

App deploy steps
----------------

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

# Use Proxy mode otherwise the egress policy is difficult to specify
# as the connection can be redirect to ports in the range of 11000 to 11999.
# See: https://docs.microsoft.com/en-us/azure/azure-sql/database/connectivity-architecture#connection-policy
az sql server conn-policy update \
  --connection-type Proxy \
  --server $SQL_SERVER_NAME \
  --resource-group $RG_NAME

az sql server conn-policy show \
    --name $SQL_SERVER_NAME \
    --resource-group $RG_NAME \

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
    --name coreDB \
    | sed -e "s/<username>/$ADMIN_USERNAME/" -e "s/<password>/$ADMIN_PASSWORD/" -e "s/\"//g")
DB_CONN_STRING="\"${DB_CONN_STRING}TrustServerCertificate=True;MultiSubnetFailover=True;\""

export DB_CONN_STRING
cat appsettings.Production.json.template | envsubst > appsettings.Production.json

# Target SQL Server (instead of SQLite) dialect
# (Only needs to be done once)
rm -rf Migrations

# Create schema on new database instance
ASPNETCORE_ENVIRONMENT=Production dotnet ef migrations add InitialCreate

# Apply schema migrations to database
ASPNETCORE_ENVIRONMENT=Production dotnet ef database update
```

Create Azure Container Registry:

```sh
ACR_NAME="todoapp$RANDOM"

az acr create --resource-group $RG_NAME \
  --name $ACR_NAME --sku Basic --admin-enabled
```

Build todoapp container image in ACR:

```sh
az acr build --image todoapp:v1 \
  --registry $ACR_NAME \
  --file Dockerfile .
```

Build timeserver container image in ACR:

```sh
az acr build --image timeserver:v1 \
  --registry $ACR_NAME \
  --file apis/timeserver/Dockerfile ./apis/timeserver
```

STEP 0 - Deploy app (e.g. if you reset the demo or if it's your first time deploying the app)
--------------------------------------------------------------------------------------

Create Image Pull Secret(s):

```sh
kubectl create ns todoapp
kubectl create ns todoapis

ACR_USER=$(az acr credential show -n $ACR_NAME --query username -o tsv)
ACR_PASSWORD=$(az acr credential show -n $ACR_NAME --query passwords[0].value -o tsv)

kubectl create secret docker-registry todo-registry --docker-server=$ACR_NAME.azurecr.io --docker-username=$ACR_USER --docker-password=$ACR_PASSWORD --docker-email=admin@example.com -n todoapp

kubectl create secret docker-registry todo-registry --docker-server=$ACR_NAME.azurecr.io --docker-username=$ACR_USER --docker-password=$ACR_PASSWORD --docker-email=admin@example.com -n todoapis
```

Deploy todoapp to Kubernetes:

```sh
kubectl create secret generic appsettings --from-file=appsettings.Production.json -n todoapp

export ACR_NAME
cat Kubernetes/todoapp.deploy.yaml | envsubst | kubectl apply -f - -n todoapp
kubectl apply -f Kubernetes/todoapp.svc.yaml -n todoapp

kubectl get pod -n todoapp
```

Deploy timeserver API to Kubernetes:

```sh
cat Kubernetes/timeserver.deploy.yaml | envsubst | kubectl apply -f - -n todoapis
kubectl apply -f Kubernetes/timeserver.svc.yaml -n todoapis

kubectl get pod -n todoapis
```

Test that the Todo App is working by port forwarding directly to the app:

```sh
kubectl port-forward svc/todoapp 8080:8080 -n todoapp
```

Browse to: http://localhost:8080

Stop port forwarding by pressing `CTRL+C`.

Set up the ingress endpoint:

```sh
# Get the ingress service public ip
INGRESS_IP=$(kubectl get svc osm-contour-envoy -n $osm_namespace -o jsonpath="{.status.loadBalancer.ingress[*].ip}")

# Name to associate with public IP address
DNSNAME=todoapp$RANDOM

# Get the resource-id of the public ip
PUBLICIPID=$(az network public-ip list --query "[?ipAddress!=null]|[?contains(ipAddress, '$INGRESS_IP')].[id]" --output tsv)

# Update public ip address with dns name
az network public-ip update --ids $PUBLICIPID --dns-name $DNSNAME

# Get the FQDN for the ingress endpoint
INGRESS_FQDN=$(az network public-ip show --ids $PUBLICIPID --query dnsSettings.fqdn -o tsv)
```

Deploy the ingress configuration:

```sh
export INGRESS_FQDN
cat Kubernetes/todoapp.httpproxy.yaml | envsubst | kubectl apply -f - -n todoapp
echo http://$INGRESS_FQDN
```

STEP 1 - Start here for demo
----------------------------

Browse to: `http://$INGRESS_FQDN`

STEP 2 - Onboard the app to the service mesh
--------------------------------------------

Verify OSM is installed:

```sh
kubectl get pod -n osm-system

# OSM CLI
osm help
```

```sh
# Onboard the app namespaces into the mesh
osm namespace add todoapp
osm namespace add todoapis
osm namespace list

# Existing pods have 1 container (app only; no sidecar)
kubectl get pod -n todoapp
kubectl get pod -n todoapis

kubectl rollout restart -n todoapp deploy/todoapp
kubectl rollout restart -n todoapis deploy/timeserver

# New pods have 2 containers (app + sidecar)
kubectl get pod -n todoapp
kubectl get pod -n todoapis

kubectl get pod -o json -n todoapp | jq '{container: .items[].spec.containers[].name }'
kubectl get pod -o json -n todoapis | jq '{container: .items[].spec.containers[].name }'
```

Browse to: `http://$INGRESS_FQDN`

RESULT: The todoapp should not work:

It needs a IngressBackend policy defined so the Contour ingress can access services in the mesh

```sh
kubectl apply -f Kubernetes/todoapp.ingress.backend.yaml
```

Browse to: `http://$INGRESS_FQDN`, the Todo app should now display with an expected error (DB access is denied due to egress being disabled).

STEP 3 - Define egress policy for external DB access
----------------------------------------------------

OSM has Egress disabled by default so the app can't access Azure SQL DB.
Rather then enable egress globally, you can enable egress on a case-by-case basis.

To fix thie egress error you can grant the `todo` app egress access to Azure SQL DB:

```sh
kubectl apply -f Kubernetes/todoapp.egresspolicy.yaml
```

Browse to: `http://$INGRESS_FQDN`, the Todo app should now work as expected.

Step 4 - Define service-to-service access policies
--------------------------------------------------

First, disable permissive traffic policy mode:

```sh
kubectl patch meshconfig osm-mesh-config -n osm-system -p '{"spec":{"traffic":{"enablePermissiveTrafficPolicyMode":false}}}' --type=merge
```

Browser displays: "Time server is currently unavailable" where the current time used to appear.

This is because all service-to-service traffic in the mesh is now denied by default.

Now define an access policy to permit the todoapp to access the timeserver:

```sh
kubectl apply -f Kubernetes/timeserver-accesspolicy.yaml
```

Browser shows: "Current time is: dd MMM YY HH:mm UTC"

STEP 6 - Traffic Split
----------------------

Create some basic styling changes to the todoapp and publish a new container image version:

* Style changes in file `Views/Todos/Index.cshtml`

Change:

```html
<h2>@(Model.TimeOfDay)</h2>
...
<table class="table">
```

to:

```html
<h2 class="alert alert-info">@(Model.TimeOfDay)</h2>
...
<table class="table table-bordered table-dark table-striped">
```

* Style changes in file `wwwroot/css/site.css`

Append to the end of the file:

```css
a {
  color: #fff;
}

a:hover {
  color: #fff;
}
```

Save changes then rebuild the container image, pushing it to the registry with the `v2` tag:

```sh
az acr build --image todoapp:v2 \
  --registry $ACR_NAME \
  --file Dockerfile .
```

Deploy v2 of the todoapp:

```sh
cat Kubernetes/todoapp-v2.deploy.yaml | envsubst | kubectl apply -f -

kubectl get deploy --namespace todoapp
```

Create a traffic split 50:50 for v1 and v2 of todoapp:

```sh
kubectl apply -f Kubernetes/todoapp.trafficsplit.yaml

kubectl get service --namespace todoapp
kubectl describe trafficsplit todoapp -n todoapp
```

Browse to: `http://$INGRESS_FQDN` and keep reloading the page.

You should see the page change styles approximately 50% of the time, corresponding to our traffic split between v1 and v2.

**TODO:** Traffic Split doesn't seem to be working in this demo!

* For a workig example, see: https://github.com/clarenceb/bookstore#traffic-split-with-smi

STEP 7 - OSM observability
--------------------------

Open Service Mesh (OSM) generates detailed metrics related to all traffic within the mesh. These metrics provide insights into the behavior of applications in the mesh helping users to troubleshoot, maintain, and analyze their applications.

Enable OSM metrics scraping for Prometheus server:

```sh
osm metrics enable --namespace todoapp
osm metrics enable --namespace todoapis
```

* Grafana

Port forward to Grafana and log in (user: admin, password: admin):

```sh
kubectl --namespace osm-system port-forward svc/osm-grafana 3000:3000
```

In Grafana on the home page, click on search (magnifying glas icon).

Choose either:

* "OSM Control Plane" to view control plane dashboards

  * View the "OSM Data plane Performance Metrics" dashboard

* "OSM Data Plane" to view data plane dashboards

  * View the "OSM Data plane Performance Metrics" dashboard

Adjust the time period on each dashboard (e.g. last 15 mins) and try changing the selected namespaces.

* Jaeger

Configure OSM for tracing with Jaeger:

```sh
kubectl get meshconfig osm-mesh-config -n osm-system

kubectl patch meshconfig osm-mesh-config -n osm-system -p '{"spec":{"observability":{"tracing":{"enable":true,"address": "jaeger.osm-system.svc.cluster.local","port":9411,"endpoint":"/api/v2/spans"}}}}'  --type=merge
```

Port forward to the Jaeger instance to view some traces:

```sh
JAEGER_POD=$(kubectl get pod -n osm-system --selector "app=jaeger" -o name)
kubectl port-forward -n osm-system $JAEGER_POD 16686:16686
# CTRL+C to exit
```

Browse to: http://localhost:16686/

Select:

* Service: `todoapp.`
* Click button `Find Traces`
* Select one of the traces to view the tracing spans
* Expand Tags to view the metadata associated with the span
* Click Deep Dependency Graph to to view dependency relationships
* Click System Architecture / DAG to view the call graph

Reset demo
----------

```sh
# Remove access and egress policies
kubectl delete -f Kubernetes/todoapp.egresspolicy.yaml
kubectl delete -f Kubernetes/timeserver-accesspolicy.yaml

# Remove traffic split and v2 of todoapp
kubectl delete -f Kubernetes/todoapp.trafficsplit.yaml
kubectl delete -f Kubernetes/todoapp-v2.deploy.yaml

# Remove todoapp and todoapi from the mesh
osm namespace remove todoapis
osm namespace remove todoapp

# Enable permissive traffic policy mode
kubectl patch meshconfig osm-mesh-config -n osm-system -p '{"spec":{"traffic":{"enablePermissiveTrafficPolicyMode":true}}}' --type=merge

# Restart app to run without the mesh
kubectl rollout restart -n todoapis deploy/timeserver
kubectl rollout restart -n todoapp deploy/todoapp
```

Full cleanup
------------

```sh
az group delete -n $RG_NAME
```

Resources
---------

* [OSM main site](https://openservicemesh.io)
* [Tutorial: Build an ASP.NET Core and Azure SQL Database app in Azure App Service](https://docs.microsoft.com/en-us/azure/app-service/tutorial-dotnetcore-sqldb-app?pivots=platform-linux) - the original app code for the Todo app
* [Setup OSM](https://release-v1-0.docs.openservicemesh.io/docs/getting_started/setup_osm/) - self-installed version based on the OSS distribution
* [Ingress with Contour](https://release-v1-0.docs.openservicemesh.io/docs/demos/ingress_contour/)
