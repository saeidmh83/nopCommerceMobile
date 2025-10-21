# معماری فنی سیستم رزرو آرایشگاه

## 1. معماری کلی سیستم

### 1.1 معماری میکروسرویس

```
┌─────────────────────────────────────────────────────────────┐
│                    Load Balancer (Nginx/HAProxy)            │
└─────────────────────────────────────────────────────────────┘
                                │
                ┌───────────────┼───────────────┐
                │               │               │
        ┌───────▼──────┐ ┌─────▼─────┐ ┌──────▼──────┐
        │   Web App    │ │ Mobile API│ │ Admin Panel │
        │  (React.js)  │ │(REST/GQL) │ │ (React.js)  │
        └──────────────┘ └───────────┘ └─────────────┘
                                │
        ┌───────────────────────┼───────────────────────┐
        │                       │                       │
┌───────▼──────┐    ┌──────────▼──────────┐    ┌──────▼──────┐
│ User Service │    │   Booking Service   │    │Payment Svc  │
│              │    │                     │    │             │
└──────────────┘    └─────────────────────┘    └─────────────┘
        │                       │                       │
┌───────▼──────┐    ┌──────────▼──────────┐    ┌──────▼──────┐
│Notification  │    │   Salon Service     │    │Analytics    │
│   Service    │    │                     │    │  Service    │
└──────────────┘    └─────────────────────┘    └─────────────┘
                                │
        ┌───────────────────────┼───────────────────────┐
        │                       │                       │
┌───────▼──────┐    ┌──────────▼──────────┐    ┌──────▼──────┐
│   Database   │    │      Redis Cache    │    │File Storage │
│ (PostgreSQL) │    │                     │    │   (MinIO)   │
└──────────────┘    └─────────────────────┘    └─────────────┘
```

### 1.2 جزئیات سرویس‌ها

#### User Service
- مدیریت کاربران (مشتریان، آرایشگران، مدیران)
- احراز هویت و مجوزدهی
- پروفایل کاربری
- سیستم امتیازدهی

#### Booking Service
- مدیریت رزروها
- تقویم و زمان‌بندی
- تایید/رد نوبت‌ها
- مدیریت صف انتظار

#### Salon Service
- مدیریت آرایشگاه‌ها
- خدمات و قیمت‌گذاری
- گالری تصاویر
- موقعیت جغرافیایی

#### Payment Service
- پردازش پرداخت‌ها
- مدیریت فاکتورها
- کیف پول
- گزارش‌های مالی

#### Notification Service
- ارسال پیامک
- اعلان‌های Push
- ایمیل
- اعلان‌های درون‌برنامه‌ای

## 2. پایگاه داده

### 2.1 طراحی پایگاه داده

```sql
-- جدول کاربران
CREATE TABLE users (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    phone_number VARCHAR(15) UNIQUE NOT NULL,
    email VARCHAR(255) UNIQUE,
    first_name VARCHAR(100),
    last_name VARCHAR(100),
    avatar_url TEXT,
    user_type ENUM('customer', 'stylist', 'salon_owner', 'admin'),
    is_active BOOLEAN DEFAULT true,
    is_verified BOOLEAN DEFAULT false,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- جدول آرایشگاه‌ها
CREATE TABLE salons (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    owner_id UUID REFERENCES users(id),
    name VARCHAR(200) NOT NULL,
    description TEXT,
    address TEXT,
    latitude DECIMAL(10, 8),
    longitude DECIMAL(11, 8),
    phone_number VARCHAR(15),
    email VARCHAR(255),
    website_url TEXT,
    instagram_url TEXT,
    license_number VARCHAR(100),
    is_verified BOOLEAN DEFAULT false,
    is_active BOOLEAN DEFAULT true,
    rating DECIMAL(3, 2) DEFAULT 0,
    review_count INTEGER DEFAULT 0,
    subscription_plan ENUM('free', 'silver', 'gold', 'platinum'),
    subscription_expires_at TIMESTAMP,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- جدول آرایشگران
CREATE TABLE stylists (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID REFERENCES users(id),
    salon_id UUID REFERENCES salons(id),
    specializations TEXT[],
    experience_years INTEGER,
    bio TEXT,
    is_independent BOOLEAN DEFAULT false,
    is_active BOOLEAN DEFAULT true,
    rating DECIMAL(3, 2) DEFAULT 0,
    review_count INTEGER DEFAULT 0,
    hourly_rate DECIMAL(10, 2),
    commission_rate DECIMAL(5, 2),
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- جدول خدمات
CREATE TABLE services (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    salon_id UUID REFERENCES salons(id),
    stylist_id UUID REFERENCES stylists(id),
    name VARCHAR(200) NOT NULL,
    description TEXT,
    category VARCHAR(100),
    duration_minutes INTEGER NOT NULL,
    base_price DECIMAL(10, 2) NOT NULL,
    capacity INTEGER DEFAULT 1,
    is_active BOOLEAN DEFAULT true,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- جدول رزروها
CREATE TABLE bookings (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    customer_id UUID REFERENCES users(id),
    salon_id UUID REFERENCES salons(id),
    stylist_id UUID REFERENCES stylists(id),
    service_ids UUID[],
    booking_date DATE NOT NULL,
    start_time TIME NOT NULL,
    end_time TIME NOT NULL,
    status ENUM('pending', 'confirmed', 'in_progress', 'completed', 'cancelled', 'no_show'),
    total_amount DECIMAL(10, 2),
    discount_amount DECIMAL(10, 2) DEFAULT 0,
    final_amount DECIMAL(10, 2),
    notes TEXT,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- جدول پرداخت‌ها
CREATE TABLE payments (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id UUID REFERENCES bookings(id),
    user_id UUID REFERENCES users(id),
    amount DECIMAL(10, 2) NOT NULL,
    payment_method ENUM('online', 'cash', 'card', 'wallet'),
    payment_gateway VARCHAR(50),
    transaction_id VARCHAR(255),
    status ENUM('pending', 'completed', 'failed', 'refunded'),
    paid_at TIMESTAMP,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- جدول نظرات و امتیازات
CREATE TABLE reviews (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id UUID REFERENCES bookings(id),
    customer_id UUID REFERENCES users(id),
    salon_id UUID REFERENCES salons(id),
    stylist_id UUID REFERENCES stylists(id),
    rating INTEGER CHECK (rating >= 1 AND rating <= 5),
    comment TEXT,
    is_approved BOOLEAN DEFAULT false,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);
```

### 2.2 ایندکس‌های بهینه‌سازی

```sql
-- ایندکس‌های عملکردی
CREATE INDEX idx_users_phone ON users(phone_number);
CREATE INDEX idx_users_type ON users(user_type);
CREATE INDEX idx_salons_location ON salons USING GIST (ST_Point(longitude, latitude));
CREATE INDEX idx_salons_rating ON salons(rating DESC);
CREATE INDEX idx_bookings_date ON bookings(booking_date, start_time);
CREATE INDEX idx_bookings_status ON bookings(status);
CREATE INDEX idx_bookings_salon ON bookings(salon_id, booking_date);
CREATE INDEX idx_reviews_rating ON reviews(salon_id, rating);
```

## 3. API Design

### 3.1 RESTful API Structure

```
/api/v1/
├── auth/
│   ├── POST /login
│   ├── POST /register
│   ├── POST /verify-otp
│   └── POST /refresh-token
├── users/
│   ├── GET /profile
│   ├── PUT /profile
│   ├── POST /avatar
│   └── GET /favorites
├── salons/
│   ├── GET /
│   ├── GET /{id}
│   ├── POST /
│   ├── PUT /{id}
│   ├── GET /{id}/services
│   ├── GET /{id}/stylists
│   └── GET /{id}/reviews
├── bookings/
│   ├── GET /
│   ├── POST /
│   ├── GET /{id}
│   ├── PUT /{id}
│   ├── DELETE /{id}
│   └── POST /{id}/cancel
├── payments/
│   ├── POST /
│   ├── GET /{id}
│   ├── POST /verify
│   └── GET /history
└── notifications/
    ├── GET /
    ├── PUT /{id}/read
    └── POST /preferences
```

### 3.2 GraphQL Schema

```graphql
type User {
  id: ID!
  phoneNumber: String!
  email: String
  firstName: String
  lastName: String
  avatarUrl: String
  userType: UserType!
  isActive: Boolean!
  isVerified: Boolean!
  createdAt: DateTime!
}

type Salon {
  id: ID!
  owner: User!
  name: String!
  description: String
  address: String
  location: Location
  phoneNumber: String
  email: String
  rating: Float!
  reviewCount: Int!
  services: [Service!]!
  stylists: [Stylist!]!
  gallery: [Image!]!
  subscriptionPlan: SubscriptionPlan!
}

type Service {
  id: ID!
  salon: Salon
  stylist: Stylist
  name: String!
  description: String
  category: String
  duration: Int!
  basePrice: Float!
  capacity: Int!
  isActive: Boolean!
}

type Booking {
  id: ID!
  customer: User!
  salon: Salon!
  stylist: Stylist
  services: [Service!]!
  bookingDate: Date!
  startTime: Time!
  endTime: Time!
  status: BookingStatus!
  totalAmount: Float!
  finalAmount: Float!
  payment: Payment
}

type Query {
  salons(filter: SalonFilter, sort: SalonSort, pagination: Pagination): SalonConnection!
  salon(id: ID!): Salon
  bookings(filter: BookingFilter): [Booking!]!
  availableSlots(salonId: ID!, serviceIds: [ID!]!, date: Date!): [TimeSlot!]!
}

type Mutation {
  createBooking(input: CreateBookingInput!): Booking!
  updateBooking(id: ID!, input: UpdateBookingInput!): Booking!
  cancelBooking(id: ID!, reason: String): Booking!
  confirmBooking(id: ID!): Booking!
}
```

## 4. معماری Frontend

### 4.1 React.js Application Structure

```
src/
├── components/
│   ├── common/
│   │   ├── Header/
│   │   ├── Footer/
│   │   ├── Loading/
│   │   └── Modal/
│   ├── salon/
│   │   ├── SalonCard/
│   │   ├── SalonDetails/
│   │   └── SalonGallery/
│   ├── booking/
│   │   ├── BookingForm/
│   │   ├── TimeSlotPicker/
│   │   └── ServiceSelector/
│   └── user/
│       ├── Profile/
│       ├── LoginForm/
│       └── Dashboard/
├── pages/
│   ├── Home/
│   ├── SalonList/
│   ├── SalonDetails/
│   ├── Booking/
│   ├── Profile/
│   └── Dashboard/
├── hooks/
│   ├── useAuth.js
│   ├── useBooking.js
│   ├── useSalons.js
│   └── useNotifications.js
├── services/
│   ├── api.js
│   ├── auth.js
│   ├── booking.js
│   └── payment.js
├── store/
│   ├── slices/
│   │   ├── authSlice.js
│   │   ├── salonSlice.js
│   │   └── bookingSlice.js
│   └── store.js
├── utils/
│   ├── constants.js
│   ├── helpers.js
│   └── validators.js
└── styles/
    ├── globals.css
    ├── components/
    └── pages/
```

### 4.2 State Management (Redux Toolkit)

```javascript
// store/slices/authSlice.js
import { createSlice, createAsyncThunk } from '@reduxjs/toolkit';

export const loginUser = createAsyncThunk(
  'auth/login',
  async ({ phoneNumber, otp }, { rejectWithValue }) => {
    try {
      const response = await authAPI.login(phoneNumber, otp);
      return response.data;
    } catch (error) {
      return rejectWithValue(error.response.data);
    }
  }
);

const authSlice = createSlice({
  name: 'auth',
  initialState: {
    user: null,
    token: null,
    isLoading: false,
    error: null,
  },
  reducers: {
    logout: (state) => {
      state.user = null;
      state.token = null;
    },
    clearError: (state) => {
      state.error = null;
    },
  },
  extraReducers: (builder) => {
    builder
      .addCase(loginUser.pending, (state) => {
        state.isLoading = true;
        state.error = null;
      })
      .addCase(loginUser.fulfilled, (state, action) => {
        state.isLoading = false;
        state.user = action.payload.user;
        state.token = action.payload.token;
      })
      .addCase(loginUser.rejected, (state, action) => {
        state.isLoading = false;
        state.error = action.payload.message;
      });
  },
});
```

## 5. Mobile Application (React Native)

### 5.1 Project Structure

```
mobile/
├── src/
│   ├── components/
│   ├── screens/
│   ├── navigation/
│   ├── services/
│   ├── hooks/
│   ├── store/
│   └── utils/
├── android/
├── ios/
└── package.json
```

### 5.2 Navigation Structure

```javascript
// navigation/AppNavigator.js
import { NavigationContainer } from '@react-navigation/native';
import { createStackNavigator } from '@react-navigation/stack';
import { createBottomTabNavigator } from '@react-navigation/bottom-tabs';

const Tab = createBottomTabNavigator();
const Stack = createStackNavigator();

function TabNavigator() {
  return (
    <Tab.Navigator>
      <Tab.Screen name="Home" component={HomeScreen} />
      <Tab.Screen name="Search" component={SearchScreen} />
      <Tab.Screen name="Bookings" component={BookingsScreen} />
      <Tab.Screen name="Profile" component={ProfileScreen} />
    </Tab.Navigator>
  );
}

function AppNavigator() {
  return (
    <NavigationContainer>
      <Stack.Navigator>
        <Stack.Screen name="Auth" component={AuthScreen} />
        <Stack.Screen name="Main" component={TabNavigator} />
        <Stack.Screen name="SalonDetails" component={SalonDetailsScreen} />
        <Stack.Screen name="Booking" component={BookingScreen} />
      </Stack.Navigator>
    </NavigationContainer>
  );
}
```

## 6. DevOps و Infrastructure

### 6.1 Docker Configuration

```dockerfile
# Dockerfile for Backend Service
FROM node:18-alpine

WORKDIR /app

COPY package*.json ./
RUN npm ci --only=production

COPY . .

EXPOSE 3000

CMD ["npm", "start"]
```

### 6.2 Docker Compose

```yaml
version: '3.8'

services:
  postgres:
    image: postgres:15
    environment:
      POSTGRES_DB: salon_booking
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: password
    volumes:
      - postgres_data:/var/lib/postgresql/data
    ports:
      - "5432:5432"

  redis:
    image: redis:7-alpine
    ports:
      - "6379:6379"

  backend:
    build: ./backend
    ports:
      - "3000:3000"
    environment:
      DATABASE_URL: postgresql://postgres:password@postgres:5432/salon_booking
      REDIS_URL: redis://redis:6379
    depends_on:
      - postgres
      - redis

  frontend:
    build: ./frontend
    ports:
      - "3001:3000"
    environment:
      REACT_APP_API_URL: http://localhost:3000/api

volumes:
  postgres_data:
```

### 6.3 Kubernetes Deployment

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: salon-booking-backend
spec:
  replicas: 3
  selector:
    matchLabels:
      app: salon-booking-backend
  template:
    metadata:
      labels:
        app: salon-booking-backend
    spec:
      containers:
      - name: backend
        image: salon-booking/backend:latest
        ports:
        - containerPort: 3000
        env:
        - name: DATABASE_URL
          valueFrom:
            secretKeyRef:
              name: db-secret
              key: url
        - name: REDIS_URL
          valueFrom:
            secretKeyRef:
              name: redis-secret
              key: url
---
apiVersion: v1
kind: Service
metadata:
  name: salon-booking-service
spec:
  selector:
    app: salon-booking-backend
  ports:
  - protocol: TCP
    port: 80
    targetPort: 3000
  type: LoadBalancer
```

## 7. مانیتورینگ و Observability

### 7.1 Prometheus Configuration

```yaml
global:
  scrape_interval: 15s

scrape_configs:
  - job_name: 'salon-booking-backend'
    static_configs:
      - targets: ['backend:3000']
    metrics_path: /metrics
    scrape_interval: 5s

  - job_name: 'postgres'
    static_configs:
      - targets: ['postgres-exporter:9187']

  - job_name: 'redis'
    static_configs:
      - targets: ['redis-exporter:9121']
```

### 7.2 Grafana Dashboard

```json
{
  "dashboard": {
    "title": "Salon Booking System",
    "panels": [
      {
        "title": "Request Rate",
        "type": "graph",
        "targets": [
          {
            "expr": "rate(http_requests_total[5m])",
            "legendFormat": "{{method}} {{route}}"
          }
        ]
      },
      {
        "title": "Response Time",
        "type": "graph",
        "targets": [
          {
            "expr": "histogram_quantile(0.95, rate(http_request_duration_seconds_bucket[5m]))",
            "legendFormat": "95th percentile"
          }
        ]
      },
      {
        "title": "Active Bookings",
        "type": "stat",
        "targets": [
          {
            "expr": "booking_active_total",
            "legendFormat": "Active Bookings"
          }
        ]
      }
    ]
  }
}
```

## 8. امنیت سیستم

### 8.1 JWT Authentication

```javascript
// middleware/auth.js
const jwt = require('jsonwebtoken');
const { promisify } = require('util');

const authenticateToken = async (req, res, next) => {
  try {
    const authHeader = req.headers['authorization'];
    const token = authHeader && authHeader.split(' ')[1];

    if (!token) {
      return res.status(401).json({ error: 'Access token required' });
    }

    const decoded = await promisify(jwt.verify)(token, process.env.JWT_SECRET);
    req.user = decoded;
    next();
  } catch (error) {
    return res.status(403).json({ error: 'Invalid token' });
  }
};

module.exports = { authenticateToken };
```

### 8.2 Rate Limiting

```javascript
// middleware/rateLimiter.js
const rateLimit = require('express-rate-limit');
const RedisStore = require('rate-limit-redis');
const redis = require('../config/redis');

const createRateLimiter = (windowMs, max, message) => {
  return rateLimit({
    store: new RedisStore({
      client: redis,
      prefix: 'rl:',
    }),
    windowMs,
    max,
    message: { error: message },
    standardHeaders: true,
    legacyHeaders: false,
  });
};

const authLimiter = createRateLimiter(
  15 * 60 * 1000, // 15 minutes
  5, // 5 attempts
  'Too many authentication attempts'
);

const apiLimiter = createRateLimiter(
  15 * 60 * 1000, // 15 minutes
  100, // 100 requests
  'Too many requests'
);

module.exports = { authLimiter, apiLimiter };
```

## 9. تست و کیفیت کد

### 9.1 Unit Testing

```javascript
// tests/services/booking.test.js
const { BookingService } = require('../../src/services/booking');
const { mockBookingData } = require('../fixtures/booking');

describe('BookingService', () => {
  let bookingService;

  beforeEach(() => {
    bookingService = new BookingService();
  });

  describe('createBooking', () => {
    it('should create a booking successfully', async () => {
      const bookingData = mockBookingData.valid;
      const result = await bookingService.createBooking(bookingData);

      expect(result).toHaveProperty('id');
      expect(result.status).toBe('pending');
      expect(result.customerId).toBe(bookingData.customerId);
    });

    it('should throw error for invalid time slot', async () => {
      const bookingData = mockBookingData.invalidTime;
      
      await expect(
        bookingService.createBooking(bookingData)
      ).rejects.toThrow('Time slot not available');
    });
  });
});
```

### 9.2 Integration Testing

```javascript
// tests/integration/booking.test.js
const request = require('supertest');
const app = require('../../src/app');
const { setupTestDB, cleanupTestDB } = require('../helpers/database');

describe('Booking API', () => {
  beforeAll(async () => {
    await setupTestDB();
  });

  afterAll(async () => {
    await cleanupTestDB();
  });

  describe('POST /api/v1/bookings', () => {
    it('should create a new booking', async () => {
      const bookingData = {
        salonId: 'salon-uuid',
        serviceIds: ['service-uuid'],
        bookingDate: '2024-01-15',
        startTime: '10:00'
      };

      const response = await request(app)
        .post('/api/v1/bookings')
        .set('Authorization', `Bearer ${validToken}`)
        .send(bookingData)
        .expect(201);

      expect(response.body).toHaveProperty('id');
      expect(response.body.status).toBe('pending');
    });
  });
});
```

## 10. Performance Optimization

### 10.1 Database Optimization

```sql
-- Partitioning for bookings table
CREATE TABLE bookings_2024_01 PARTITION OF bookings
FOR VALUES FROM ('2024-01-01') TO ('2024-02-01');

CREATE TABLE bookings_2024_02 PARTITION OF bookings
FOR VALUES FROM ('2024-02-01') TO ('2024-03-01');

-- Materialized view for salon statistics
CREATE MATERIALIZED VIEW salon_stats AS
SELECT 
    s.id,
    s.name,
    AVG(r.rating) as avg_rating,
    COUNT(r.id) as review_count,
    COUNT(DISTINCT b.customer_id) as unique_customers,
    SUM(b.final_amount) as total_revenue
FROM salons s
LEFT JOIN reviews r ON s.id = r.salon_id
LEFT JOIN bookings b ON s.id = b.salon_id
WHERE b.status = 'completed'
GROUP BY s.id, s.name;

-- Refresh materialized view daily
SELECT cron.schedule('refresh-salon-stats', '0 2 * * *', 'REFRESH MATERIALIZED VIEW salon_stats;');
```

### 10.2 Caching Strategy

```javascript
// services/cache.js
const Redis = require('redis');
const redis = Redis.createClient(process.env.REDIS_URL);

class CacheService {
  async get(key) {
    try {
      const value = await redis.get(key);
      return value ? JSON.parse(value) : null;
    } catch (error) {
      console.error('Cache get error:', error);
      return null;
    }
  }

  async set(key, value, ttl = 3600) {
    try {
      await redis.setex(key, ttl, JSON.stringify(value));
    } catch (error) {
      console.error('Cache set error:', error);
    }
  }

  async del(key) {
    try {
      await redis.del(key);
    } catch (error) {
      console.error('Cache delete error:', error);
    }
  }

  // Cache popular salons
  async getPopularSalons() {
    const cacheKey = 'popular_salons';
    let salons = await this.get(cacheKey);
    
    if (!salons) {
      salons = await SalonService.getPopularSalons();
      await this.set(cacheKey, salons, 1800); // 30 minutes
    }
    
    return salons;
  }
}

module.exports = new CacheService();
```

این معماری فنی جامع و مقیاس‌پذیر است که می‌تواند نیازهای فعلی و آینده سیستم رزرو آرایشگاه را پاسخ دهد.