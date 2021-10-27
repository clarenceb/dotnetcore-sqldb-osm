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
NODE_COUNT=3

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
kubectl patch meshconfig osm-mesh-config -n kube-system -p '{"spec":{"traffic":{"enablePermissiveTrafficPolicyMode":true}}}' --type=merge
kubectl patch meshconfig osm-mesh-config -n kube-system -p '{"spec":{"traffic":{"enableEgress":false}}}' --type=merge
kubectl patch meshconfig osm-mesh-config -n kube-system -p '{"spec":{"featureFlags":{"enableEgressPolicy":false}}}' --type=merge

kubectl rollout restart deploy/osm-controller -n kube-system
kubectl rollout restart deploy/osm-injector -n kube-system
```

Install OSM client library
--------------------------

```sh
OSM_VERSION=v0.9.2
curl -sL "https://github.com/openservicemesh/osm/releases/download/$OSM_VERSION/osm-$OSM_VERSION-linux-amd64.tar.gz" | tar -vxzf -
sudo mv ./linux-amd64/osm /usr/local/bin/osm
osm version
```

Build and deploy the application
--------------------------------

Build image in ACR:

```sh
az acr build --image todoapp:v6 \
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

Create Image Pull Secret(s):

```sh
kubectl create ns todoapp
kubectl create ns todoapis

ACR_USER=$(az acr credential show -n $ACR_NAME --query username -o tsv)
ACR_PASSWORD=$(az acr credential show -n $ACR_NAME --query passwords[0].value -o tsv)

kubectl create secret docker-registry todo-registry --docker-server=$ACR_NAME.azurecr.io --docker-username=$ACR_USER --docker-password=$ACR_PASSWORD --docker-email=admin@example.com -n todoapp

kubectl create secret docker-registry todo-registry --docker-server=$ACR_NAME.azurecr.io --docker-username=$ACR_USER --docker-password=$ACR_PASSWORD --docker-email=admin@example.com -n todoapis
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

Onboard todoapp to OSM
----------------------

```sh
osm namespace add todoapp
osm namespace list
kubectl get pod -n todoapp

kubectl rollout restart -n todoapp deploy/todoapp
kubectl get pod -n todoapp
```

The todoapp should not work as it needs egress to Azure SQL DB.

Error displayed in browser: "upstream request timeout"

We can check the pod logs:

```sh
# Todoapp
kubectl logs $(kubectl get pod -n todoapp -l app=todoapp -o jsonpath='{.items[0].metadata.name}') -n todoapp todoapp -f
# --> An error occurred using the connection to database 'coreDB' on server 'tcp:todoserver-xxxxx.database.windows.net,1433'.

# OSM sidecar
kubectl logs $(kubectl get pod -n todoapp -l app=todoapp -o jsonpath='{.items[0].metadata.name}') -n todoapp envoy -f
# --> "upstream_response_timeout"}
```

Enable egress
-------------

Enable egress traffic to allow access to Azure SQL DB:

```sh
kubectl patch meshconfig osm-mesh-config -n kube-system -p '{"spec":{"traffic":{"enableEgress":true}}}' --type=merge
kubectl rollout restart deploy/osm-controller -n kube-system
kubectl rollout restart deploy/osm-injector -n kube-system

kubectl rollout restart -n todoapp deploy/todoapp
kubectl get pod -n todoapp
```

The todoapp should now work as it can connect to Azure SQL DB.

Deploy an API in the cluster
----------------------------

Build image in ACR:

```sh
az acr build --image timeserver:v1 \
  --registry $ACR_NAME \
  --file apis/timeserver/Dockerfile ./apis/timeserver
```

Deploy API(s):

```sh
osm namespace add todoapis
osm namespace list

cat Kubernetes/timeserver.deploy.yaml | envsubst | kubectl apply -f - -n todoapis
kubectl apply -f Kubernetes/timeserver.svc.yaml -n todoapis

kubectl get pod -n todoapis
```

Define access policies
----------------------

First, block all access to the api:

```sh
kubectl patch meshconfig osm-mesh-config -n kube-system -p '{"spec":{"traffic":{"enablePermissiveTrafficPolicyMode":false}}}' --type=merge
```

Browser shows: "Time unavailable: An error occurred while sending the request."

Now define an access policy just for todoapp to access the timeserver:

```sh
kubectl apply -f Kubernetes/timeserver-accesspolicy.yaml
```

Browser shows: "Current time is xxxx-xx-xx yy:yy:yy ...snip..."

Enable Azure Monitoring of OSM and your workloads
-------------------------------------------------

Refer to: https://docs.microsoft.com/en-us/azure/aks/open-service-mesh-azure-monitor

Enable OSM metrics scraping:

```sh
osm metrics enable --namespace todoapp
osm metrics enable --namespace todoapis
```

Enable Azure Monitor namespace collection and pod Prometheus metric monitoring:

```sh
kubectl apply -f Kubernetes/azmon-ns-config.yaml
```

Note: the configuration change can take upto 15 mins to finish before taking effect.

Verify metrics are visible in the Azure Portal (use this link https://aka.ms/azmon/osmux to get access to the preview OSM moniotring report):

* Navigate to your AKS cluster, "Monitoring" / "Insights" / "Reports"
* Click "OSM monitoring" under the "Networking" category

You should see the OSM monitoring workbook.

To query logs, navigate to your AKS cluster, "Monitoring" / "Logs"

Run this query:

```kql
InsightsMetrics
| where Name contains "envoy"
| extend t=parse_json(Tags)
| where t.app == "todoapp"
```

Configure observability metrics for your mesh
---------------------------------------------

Refer to: https://docs.microsoft.com/en-us/azure/aks/open-service-mesh-open-source-observability

Open Service Mesh (OSM) generates detailed metrics related to all traffic within the mesh. These metrics provide insights into the behavior of applications in the mesh helping users to troubleshoot, maintain, and analyze their applications.

Deploy and configure Prometheus server:

```sh
helm repo add prometheus-community https://prometheus-community.github.io/helm-charts
helm repo update
helm install stable prometheus-community/prometheus
```

Prometheus metrics collection is enabled automatically on namespaces that have been onboarded via `osm namespace add ...`.

Make a copy of the Prometheus server YAML:

```sh
kubectl get configmap | grep prometheus
kubectl get configmap stable-prometheus-server -o yaml > cm-stable-prometheus-server.yml
cp cm-stable-prometheus-server.yml cm-stable-prometheus-server.yml.copy
```

Update the Prometheus server YAML (copy and paste the [config](https://docs.microsoft.com/en-us/azure/aks/open-service-mesh-open-source-observability#update-the-prometheus-configmap) at `Paste prometheus.yml in here!` below):

```yaml
apiVersion: v1
data:
  alerting_rules.yml: |
    {}
  alerts: |
    {}
  recording_rules.yml: |
    {}
  # Paste prometheus.yml in here!
  rules: |
    {}
kind: ConfigMap
```

```sh
kubectl apply -f Kubernetes/cm-stable-prometheus-server.yml
```

Verify Prometheus is correctly configured to scrape OSM mesh and API endpoints:

```sh
PROM_POD_NAME=$(kubectl get pods -l "app=prometheus,component=server" -o jsonpath="{.items[0].metadata.name}")
kubectl --namespace default port-forward $PROM_POD_NAME 9090
```

Open a browser up to http://localhost:9090/targets

Deploy and configure Grafana:

```sh
helm repo add grafana https://grafana.github.io/helm-charts
helm repo update
helm install osm-grafana grafana/grafana
```

Retrieve the default Grafana password:

```sh
kubectl get secret --namespace default osm-grafana -o jsonpath="{.data.admin-password}" | base64 --decode ; echo
```

Port forward to Grafana and log in (user: admin, password: see above):

```sh
GRAF_POD_NAME=$(kubectl get pods -l "app.kubernetes.io/name=grafana" -o jsonpath="{.items[0].metadata.name}")
kubectl port-forward $GRAF_POD_NAME 3000
```

Add a datasource in Grafan for Prometheus:

* Configuration / Data Sources / Add data source ([link](http://localhost:3000/datasources/new))
* Select Prometheus
* Update URL: `stable-prometheus-server.default.svc.cluster.local`
* Click **Save & test**

Import OSM dashboard:

* Download JSON dashboards for Grafana from here: https://github.com/openservicemesh/osm/tree/release-v0.9/charts/osm/grafana/dashboards
* Click *`+`* / Import ([link](http://localhost:3000/dashboard/import))
* Select "Upload JSON file"
* Click **Load**
* Select your Prometheus data source (if prompted)
* Click Import

You will now see the Grafana dashboards for OSM.

Configure distributed tracing
-----------------------------

Follow the steps [here](https://docs.microsoft.com/en-us/azure/aks/open-service-mesh-open-source-observability#deploy-and-configure-a-jaeger-operator-on-kubernetes-for-osm).

```sh
kubectl create namespace jaeger

kubectl apply -f Kubernetes/jaeger.yaml
kubectl apply -f Kubernetes/jaeger-rbac.yaml
kubectl patch meshconfig osm-mesh-config -n kube-system -p '{"spec":{"observability":{"tracing":{"enable":true, "address": "jaeger.jaeger.svc.cluster.local"}}}}' --type=merge

JAEGER_POD=$(kubectl get pods -n jaeger --no-headers  --selector app=jaeger | awk 'NR==1{print $1}')
kubectl port-forward -n jaeger $JAEGER_POD  16686:16686
```

Browse to: http://localhost:16686/

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
* [Open Service Mesh (OSM) Monitoring and Observability using Azure Monitor and Applications Insights](https://docs.microsoft.com/en-us/azure/aks/open-service-mesh-azure-monitor)
* [Azure Monitor Container Insights Open Service Mesh Monitoring - Private Preview](https://github.com/microsoft/Docker-Provider/blob/ci_dev/Documentation/OSMPrivatePreview/ReadMe.md)
* [Manually deploy Prometheus, Grafana, and Jaeger to view Open Service Mesh (OSM) metrics for observability](https://docs.microsoft.com/en-us/azure/aks/open-service-mesh-open-source-observability)
