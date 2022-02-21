OSM Demo
========

STEP 0 - Kubernetes with OSM installed and app deployed
-------------------------------------------------------

See steps [here](./Demo.md) to:

* Create the Kubernetes cluster with OSM
* Deploy the demo app without being part of the service mesh

STEP 1 - Onboard existing app to OSM
------------------------------------

```sh
kubectl get pod -n osm-system

osm namespace add todoapp
osm namespace add todoapis
osm namespace list

kubectl rollout restart -n todoapp deploy/todoapp
kubectl rollout restart -n todoapis deploy/timeserver

kubectl get pod -o json -n todoapp | jq '{container: .items[].spec.containers[].name }'
kubectl get pod -o json -n todoapis | jq '{container: .items[].spec.containers[].name }'
```

Browse to: `http://$INGRESS_FQDN`

STEP 2 - Allow egress from todoapp to the DB
--------------------------------------------

```sh
kubectl get  meshconfig osm-mesh-config -n osm-system -o json | jq .spec.traffic

kubectl apply -f Kubernetes/todoapp.egresspolicy.yaml
```

Browse to: `http://$INGRESS_FQDN`

STEP 3 - Define service-to-service access policies
--------------------------------------------------

```sh
kubectl patch meshconfig osm-mesh-config -n osm-system -p '{"spec":{"traffic":{"enablePermissiveTrafficPolicyMode":false}}}' --type=merge
```

Browse to: `http://$INGRESS_FQDN`

```sh
kubectl apply -f Kubernetes/timeserver-accesspolicy.yaml
```

Browse to: `http://$INGRESS_FQDN`"

STEP 4 - Observability
----------------------

Create some load on the app:

```sh
URL=http://$INGRESS_FQDN/ k6 run ./k6-script.js
```

Access Grafana for monitoring/metrics (user: admin, password: admin):

```sh
kubectl --namespace osm-system port-forward svc/osm-grafana 3000:3000
```

Browse to: `http://localhost:3000`

Access Jaeger for tracing:

```sh
JAEGER_POD=$(kubectl get pod -n osm-system --selector "app=jaeger" -o name)
kubectl port-forward -n osm-system $JAEGER_POD 16686:16686
```

Browse to: `http://localhost:16686`
