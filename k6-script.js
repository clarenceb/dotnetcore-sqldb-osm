import http from 'k6/http';
import { check, sleep } from 'k6';

export let options = {
  stages: [
    { duration: '30s', target: 50 },
  ],
};

export default function() {
  let url = __ENV.URL

  let res = http.get(url);

  check(res, { 'status was 200': r => r.status == 200 });

  sleep(0.5)
}
