Add OSM to an existing app running on AKS
=========================================

This is an example application based on the [Tutorial: Build an ASP.NET Core and Azure SQL Database app in Azure App Service](https://docs.microsoft.com/en-us/azure/app-service/tutorial-dotnetcore-sqldb-app?pivots=platform-linux) which has been modified for the purposes of demonstrating some capabilities of Open Service Mesh (OSM).

* Tested on OSM v0.11.1 (via AKS add-on)

Prequisites
-----------

* Common variables (adjust as appropriate):

```sh
LOCATION="australiaeast"
RG_NAME="aks-demos"
CLUSTER="aks-demos"
NODE_COUNT=3
```

Create the Azure Resource Group to contain all Azure Resources for the demo:

```sh
az group create --name $RG_NAME --location $LOCATION
```

Create a basic AKS cluster with the OSM add-on enabled:

```sh
az provider register --namespace Microsoft.OperationsManagement
az provider register --namespace Microsoft.OperationalInsights

az provider show -n Microsoft.OperationsManagement -o table
az provider show -n Microsoft.OperationalInsights -o table

az aks create --resource-group $RG_NAME --name $CLUSTER --node-count $NODE_COUNT --enable-addons monitoring,open-service-mesh --generate-ssh-keys

az aks get-credentials --resource-group $RG_NAME --name $CLUSTER
az aks install-cli

kubectl get node -o wide
```

or enable OSM on an existing AKS cluster:

```sh
az aks enable-addons \
  --resource-group $RG_NAME \
  --name $CLUSTER \
  --addons open-service-mesh
```

Verify OSM is installed:

```sh
az aks list -g $RG_NAME -o json | jq -r '.[].addonProfiles.openServiceMesh.enabled'

kubectl get pods -n kube-system --selector app.kubernetes.io/name=openservicemesh.io
```

Ensure OpenSSL is installed locally:

```sh
sudo apt-get update -y
sudo apt-get install -y openssl
```

Install Helm 3:

* Follow the [official installation instructions](https://helm.sh/docs/intro/install/)

Demo steps
----------

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

# Target SQL Server (instead of SQLite) dialect
# (Only needs to be done once)
rm -rf Migrations
ASPNETCORE_ENVIRONMENT=Production dotnet ef migrations add InitialCreate

# Apply schema migrations to database
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

Build todo app in ACR:

```sh
az acr build --image todoapp:v1 \
  --registry $ACR_NAME \
  --file Dockerfile .
```

Build timeserver in ACR:

```sh
az acr build --image timeserver:v1 \
  --registry $ACR_NAME \
  --file apis/timeserver/Dockerfile ./apis/timeserver
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
    --set controller.ingressClassResource.controllerValue="k8s.io/ingress-nginx" \
    --version 4.0.17

kubectl wait --namespace app-ingress \
  --for=condition=ready pod \
  --selector=app.kubernetes.io/component=controller \
  --timeout=120s

# Get the ingress service public ip
INGRESS_IP=$(kubectl get svc app-ingress-ingress-nginx-controller -n app-ingress -o jsonpath="{.status.loadBalancer.ingress[*].ip}")

# Name to associate with public IP address
DNSNAME=$ACR_NAME

# Get the resource-id of the public ip
PUBLICIPID=$(az network public-ip list --query "[?ipAddress!=null]|[?contains(ipAddress, '$INGRESS_IP')].[id]" --output tsv)

# Update public ip address with dns name
az network public-ip update --ids $PUBLICIPID --dns-name $DNSNAME

# Get the FQDN for the ingress endpoint
INGRESS_FQDN=$(az network public-ip show --ids $PUBLICIPID --query dnsSettings.fqdn -o tsv)
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
kubectl create secret generic appsettings --from-file=appsettings.Production.json -n todoapp

export ACR_NAME
cat Kubernetes/todoapp.deploy.yaml | envsubst | kubectl apply -f - -n todoapp
kubectl apply -f Kubernetes/todoapp.svc.yaml -n todoapp

export INGRESS_FQDN
cat Kubernetes/todoapp.ingress.yaml | envsubst | kubectl apply -f - -n todoapp

kubectl get pod -n todoapp
```

Deploy API:

```sh
cat Kubernetes/timeserver.deploy.yaml | envsubst | kubectl apply -f - -n todoapis
kubectl apply -f Kubernetes/timeserver.svc.yaml -n todoapis

kubectl get pod -n todoapis
```

Access the Todo App and verify it is working and stores todos in Azure SQL DB:

```sh
kubectl get ingress -n todoapp
# <ingress_ip>
# Browse to: http://<ingress_ip>
```

Add TLS on ingress endpoint:

```sh
helm repo add jetstack https://charts.jetstack.io
helm repo update

# Install Cert-Manager so we can issue a free TLS certificate for our app ingress
kubectl apply --validate=false -f https://github.com/jetstack/cert-manager/releases/download/v0.14.3/cert-manager.crds.yaml
helm install \
  cert-manager jetstack/cert-manager \
  --namespace cert-manager \
  --create-namespace \
  --version v1.7.1 \
  --set installCRDs=true

kubectl get pod -n cert-manager

# Update with a validate e-mail address for Let's Encrypt to issue a certificate
export ACME_EMAIL="your@email-address"
cat Kubernetes/cert-issuer.yaml | envsubst | kubectl apply -f - -n todoapp
```

############################################

Configure OSM
-------------

```sh
kubectl get meshconfig osm-mesh-config -n kube-system -o yaml
kubectl patch meshconfig osm-mesh-config -n kube-system -p '{"spec":{"traffic":{"enablePermissiveTrafficPolicyMode":true}}}' --type=merge
kubectl patch meshconfig osm-mesh-config -n kube-system -p '{"spec":{"traffic":{"enableEgress":true}}}' --type=merge
kubectl patch meshconfig osm-mesh-config -n kube-system -p '{"spec":{"featureFlags":{"enableEgressPolicy":true}}}' --type=merge
```

Install OSM client library
--------------------------

```sh
OSM_VERSION=v0.11.1
curl -sL "https://github.com/openservicemesh/osm/releases/download/$OSM_VERSION/osm-$OSM_VERSION-linux-amd64.tar.gz" | tar -vxzf -
sudo mv ./linux-amd64/osm /usr/local/bin/osm
osm version
```

Onboard todoapp to OSM
----------------------

```sh
osm namespace add todoapp
osm namespace add todoapis
osm namespace list
kubectl get pod -n todoapp
kubectl get pod -n todoapis

kubectl rollout restart -n todoapp deploy/todoapp
kubectl rollout restart -n todoapis deploy/timeserver

kubectl get pod -n todoapp
kubectl get pod -n todoapis
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

Configure NGINX Ingress integration with OSM
--------------------------------------------

Configure FQDN for the ingress IP for SNI to work:

```sh
# Name to associate with public IP address
DNSNAME="osmdemo-$(openssl rand -hex 3)"
PUBLIC_IP=$(kubectl get svc app-ingress-ingress-nginx-controller -n app-ingress -o jsonpath="{.status.loadBalancer.ingress[0].ip}")

# Get the resource-id of the public ip
PUBLICIPID=$(az network public-ip list --query "[?ipAddress!=null]|[?contains(ipAddress, '$PUBLIC_IP')].[id]" --output tsv)

# Update public ip address with dns name
az network public-ip update --ids $PUBLICIPID --dns-name $DNSNAME
```

We can restrict ingress traffic on backends to authorised clients (i.e. NGINX ingress).

```sh
osm_namespace=kube-system
osm_mesh_name=osm

nginx_ingress_namespace=app-ingress
nginx_ingress_service=app-ingress-ingress-nginx-controller
nginx_ingress_host="$(kubectl -n "$nginx_ingress_namespace" get service "$nginx_ingress_service" -o jsonpath='{.status.loadBalancer.ingress[0].ip}')"
nginx_ingress_port="$(kubectl -n "$nginx_ingress_namespace" get service "$nginx_ingress_service" -o jsonpath='{.spec.ports[?(@.name=="http")].port}')"

kubectl label ns "$nginx_ingress_namespace" openservicemesh.io/monitored-by="$osm_mesh_name"

cat<<EOF | kubectl apply -f -
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: httpbin
  namespace: httpbin
  annotations:
    nginx.ingress.kubernetes.io/ssl-redirect: "false"
    nginx.ingress.kubernetes.io/use-regex: "true"
    nginx.ingress.kubernetes.io/rewrite-target: /$2
spec:
  ingressClassName: app-public
  rules:
  - host: osmdemo-b7c482.australiaeast.cloudapp.azure.com
    http:
      paths:
      - path: /httpbin(/|$)(.*)
        pathType: Prefix
        backend:
          service:
            name: httpbin
            port:
              number: 14001
---
kind: IngressBackend
apiVersion: policy.openservicemesh.io/v1alpha1
metadata:
  name: httpbin
  namespace: httpbin
spec:
  backends:
  - name: httpbin
    port:
      number: 14001
      protocol: http
  sources:
  - kind: Service
    namespace: "$nginx_ingress_namespace"
    name: "$nginx_ingress_service"
EOF

curl -sI http://"$nginx_ingress_host":"$nginx_ingress_port"/get

kubectl edit meshconfig osm-mesh-config -n "$osm_namespace"
```

Add the following under **certificate**:

```yaml
ingressGateway:
  secret:
    name: osm-nginx-client-cert
    namespace: kube-system
  subjectAltNames:
  - app-ingress-ingress-nginx.app-ingress.cluster.local
  validityDuration: 24h
```

```sh
kubectl patch meshconfig osm-mesh-config -n kube-system -p '{"spec":{"traffic":{"useHTTPSIngress":true}}}'  --type=merge
```

Add TLS with Cert-Manager and Let's Encrypt:

```sh
helm repo add jetstack https://charts.jetstack.io
helm repo update

kubectl create ns cert-manager

helm install \
  cert-manager jetstack/cert-manager \
  --namespace cert-manager \
  --create-namespace \
  --version v1.6.0 \
  --set installCRDs=true

kubectl apply -f Kubernetes/cert-issuer.yaml -n httpbin
```

Update Ingress to use TLS on frontend as well:

```sh
kubectl apply -f - <<EOF
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: httpbin
  namespace: httpbin
  annotations:
    nginx.ingress.kubernetes.io/ssl-redirect: "true"
    nginx.ingress.kubernetes.io/use-regex: "true"
    nginx.ingress.kubernetes.io/rewrite-target: /$2
    nginx.ingress.kubernetes.io/backend-protocol: "HTTPS"
    # proxy_ssl_name for a service is of the form <service-account>.<namespace>.cluster.local
    nginx.ingress.kubernetes.io/configuration-snippet: |
      proxy_ssl_name "httpbin.httpbin.cluster.local";
    nginx.ingress.kubernetes.io/proxy-ssl-secret: "kube-system/osm-nginx-client-cert"
    nginx.ingress.kubernetes.io/proxy-ssl-verify: "on"
    cert-manager.io/issuer: "letsencrypt-prod"
spec:
  ingressClassName: app-public
  tls:
  - hosts:
    - osmdemo-b7c482.australiaeast.cloudapp.azure.com
    secretName: httpbin-tls
  rules:
  - host: osmdemo-b7c482.australiaeast.cloudapp.azure.com
    http:
      paths:
      - path: /httpbin(/|$)(.*)
        pathType: Prefix
        backend:
          service:
            name: httpbin
            port:
              number: 14001
---
apiVersion: policy.openservicemesh.io/v1alpha1
kind: IngressBackend
metadata:
  name: httpbin
  namespace: httpbin
spec:
  backends:
  - name: httpbin
    port:
      number: 14001
      protocol: https
    tls:
      skipClientCertValidation: false
  sources:
  - kind: Service
    name: "$nginx_ingress_service"
    namespace: "$nginx_ingress_namespace"
  - kind: AuthenticatedPrincipal
    name: app-ingress-ingress-nginx.app-ingress.cluster.local
EOF

kubectl label namespace app-ingress cert-manager.io/disable-validation=true

helm upgrade app-ingress ingress-nginx/ingress-nginx \
    --namespace app-ingress \
  --set controller.service.annotations."service\.beta\.kubernetes\.io/azure-dns-label-name"=osmdemo-b7c482.australiaeast.cloudapp.azure.com
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
* [Open Service Mesh (OSM) Monitoring and Observability using Azure Monitor and Applications Insights](https://docs.microsoft.com/en-us/azure/aks/open-service-mesh-azure-monitor)
* [Azure Monitor Container Insights Open Service Mesh Monitoring - Private Preview](https://github.com/microsoft/Docker-Provider/blob/ci_dev/Documentation/OSMPrivatePreview/ReadMe.md)
* [Manually deploy Prometheus, Grafana, and Jaeger to view Open Service Mesh (OSM) metrics for observability](https://docs.microsoft.com/en-us/azure/aks/open-service-mesh-open-source-observability)
